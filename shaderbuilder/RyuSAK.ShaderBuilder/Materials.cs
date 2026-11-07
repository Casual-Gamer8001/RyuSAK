using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace RyuSAK.ShaderBuilder;

/// <summary>A shader option of a shading model, and where its choice is stored in each program's key.</summary>
sealed class ShaderOptionInfo
{
    public string Name;
    public string[] Choices;
    public int DefaultChoice;
    public int Bit32Index;
    public int Bit32Shift;
    public uint Bit32Mask;

    /// <summary>For dynamic options, where the dynamic part starts in the option's key word numbering.</summary>
    public int KeyOffset;

    public int ChoiceOf(int[] keyTable, int programBase) => (int)(((uint)keyTable[programBase + Bit32Index] & Bit32Mask) >> Bit32Shift);
}

/// <summary>A shading model from a BFSHA shader archive: its options and, per program, the option key and code.</summary>
sealed class ShadingModelInfo
{
    public string Archive;
    public string Name;
    public ShaderOptionInfo[] StaticOptions;
    public ShaderOptionInfo[] DynamicOptions;
    public int StaticKeyLength;
    public int KeyLength;
    public int[] KeyTable;
    public ProgramKey?[] Programs;

    public int[] ChoicesOf(int program)
    {
        int[] choices = new int[StaticOptions.Length];
        for (int o = 0; o < choices.Length; o++)
        {
            choices[o] = StaticOptions[o].ChoiceOf(KeyTable, program * KeyLength);
        }

        return choices;
    }

    public int[] DynamicChoicesOf(int program)
    {
        int[] choices = new int[DynamicOptions.Length];
        for (int o = 0; o < choices.Length; o++)
        {
            ShaderOptionInfo option = DynamicOptions[o];
            int word = program * KeyLength + StaticKeyLength + option.Bit32Index - option.KeyOffset;
            choices[o] = (int)(((uint)KeyTable[word] & option.Bit32Mask) >> option.Bit32Shift);
        }

        return choices;
    }
}

/// <summary>Which shader a model's material asks for (BFRES ShaderAssign), and its render settings.</summary>
sealed class MaterialInfo
{
    public string Archive;
    public string Model;
    public Dictionary<string, string> Options;
    public Dictionary<string, string> RenderInfo = new();

    /// <summary>Skin weight counts of the meshes drawn with this material (empty if unknown).</summary>
    public HashSet<int> SkinCounts = new();
}

/// <summary>
/// Reads Nintendo's BFSHA shader archives and the BFRES model materials that select programs from them, so the
/// builder can tell the programs some material can use from unused combinations in an archive. Layouts follow
/// KillzXGaming's ShaderLibrary and BfresLibrary (MIT).
/// </summary>
static class MaterialReader
{
    public static void Scan(ShaderFinder finder, byte[] buffer, int scanLimit)
    {
        int limit = Math.Min(scanLimit, buffer.Length);

        foreach (int at in Find(buffer, "FSHA"u8, 0, limit))
        {
            try
            {
                List<ShadingModelInfo> models = ReadBfsha(finder, buffer, at);
                if (models != null)
                {
                    foreach (ShadingModelInfo model in models)
                    {
                        finder.ShadingModels.Add(model);
                    }

                    finder.Count("bfsha archives");
                }
            }
            catch (Exception)
            {
                finder.Count("bfsha archives that could not be read");
            }
        }

        foreach (int at in Find(buffer, "FRES"u8, 0, limit))
        {
            try
            {
                int read = ReadBfres(finder, buffer, at);
                if (read >= 0)
                {
                    finder.Count("bfres files");
                    finder.Count("bfres materials", read);
                }
            }
            catch (Exception)
            {
                finder.Count("bfres files that could not be read");
            }
        }
    }

    private static IEnumerable<int> Find(byte[] buffer, byte[] magic, int from, int limit)
    {
        int pos = Math.Max(0, from);
        while (pos < limit)
        {
            int found = buffer.AsSpan(pos, limit - pos).IndexOf(magic);
            if (found < 0)
            {
                yield break;
            }

            yield return pos + found;
            pos += found + 1;
        }
    }

    private static IEnumerable<int> Find(byte[] buffer, ReadOnlySpan<byte> magic, int from, int limit) => Find(buffer, magic.ToArray(), from, limit);

    private static ulong U64(byte[] b, long p) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(checked((int)p)));
    private static uint U32(byte[] b, long p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(checked((int)p)));
    private static ushort U16(byte[] b, long p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(checked((int)p)));

    /// <summary>Strings are stored with a 16-bit length in front; offsets point at the length.</summary>
    private static string Str(byte[] b, int start, ulong offset)
    {
        if (offset == 0)
        {
            return null;
        }

        long p = start + (long)offset;
        int length = U16(b, p);
        if (length > 4096 || p + 2 + length > b.Length)
        {
            throw new InvalidDataException("bad string");
        }

        return Encoding.UTF8.GetString(b, (int)p + 2, length);
    }

    /// <summary>Keys of a _DIC dictionary, in value order (node 0 is the root).</summary>
    private static string[] DictKeys(byte[] b, int start, ulong offset)
    {
        if (offset == 0)
        {
            return Array.Empty<string>();
        }

        long p = start + (long)offset;
        int count = (int)U32(b, p + 4);
        if (count < 0 || count > 100_000)
        {
            throw new InvalidDataException("bad dictionary");
        }

        string[] keys = new string[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = Str(b, start, U64(b, p + 8 + (i + 1) * 16 + 8));
        }

        return keys;
    }

    private sealed class Cursor(byte[] buffer, long position)
    {
        public long Position = position;

        public ulong U64() { ulong v = MaterialReader.U64(buffer, Position); Position += 8; return v; }
        public uint U32() { uint v = MaterialReader.U32(buffer, Position); Position += 4; return v; }
        public int I32() => (int)U32();
        public ushort U16() { ushort v = MaterialReader.U16(buffer, Position); Position += 2; return v; }
        public short I16() => (short)U16();
        public byte U8() => buffer[Position++];
        public void Skip(int count) => Position += count;
    }

    private static List<ShadingModelInfo> ReadBfsha(ShaderFinder finder, byte[] b, int start)
    {
        // nn::util binary file header: magic, version, byte order mark, ... file size.
        if (start + 0x60 > b.Length)
        {
            return null;
        }

        if (U16(b, start + 0x0C) != 0xFEFF)
        {
            finder.Count("fsha rejected: byte order");
            return null;
        }

        int version = U16(b, start + 0x0A);
        uint fileSize = U32(b, start + 0x1C);
        if (version < 3 || version > 12 || fileSize < 0x60 || start + fileSize > b.Length)
        {
            finder.Count($"fsha rejected: version {version}, size {(start + fileSize > b.Length ? "past end" : "ok")}");
            return null;
        }

        finder.Count($"bfsha version {version}");
        string archive = Str(b, start, U64(b, start + 0x38));
        ulong modelValues = U64(b, start + 0x48);
        ulong modelDict = U64(b, start + 0x50);
        string[] modelNames = DictKeys(b, start, modelDict);

        List<ShadingModelInfo> models = new();
        Cursor c = new(b, start + (long)modelValues);

        for (int m = 0; m < modelNames.Length; m++)
        {
            ShadingModelInfo model = new() { Archive = archive, Name = Str(b, start, c.U64()) };
            ulong staticValues = c.U64(); c.U64();
            ulong dynamicValues = c.U64(); c.U64();
            c.U64(); c.U64(); // attributes
            c.U64(); c.U64(); // samplers
            if (version >= 8) { c.U64(); c.U64(); } // images
            c.U64(); c.U64(); // uniform blocks
            c.U64(); // uniform array
            if (version >= 7) { c.U64(); c.U64(); c.U64(); } // storage buffers
            ulong programArray = c.U64();
            ulong keyTable = c.U64();
            c.U64(); // parent archive
            c.U64(); // symbols
            ulong bnsh = c.U64();
            c.U64(); c.U64(); c.U64();
            if (version >= 7) { c.U64(); c.U64(); }
            c.U32(); // uniform count
            if (version >= 7) c.U32();
            c.I32(); // default program
            int staticCount = c.U16();
            int dynamicCount = c.U16();
            int programCount = c.U16();
            if (version < 7) c.U16();
            int staticKeyLength = c.U8();
            int dynamicKeyLength = c.U8();
            c.U8(); c.U8(); // attribute and sampler counts
            if (version >= 8) c.U8();
            c.U8(); c.U8();
            c.Skip(4);
            c.Skip(version >= 8 ? 11 : version >= 7 ? 4 : 6);

            model.StaticOptions = ReadOptions(b, start, staticValues, staticCount, version);
            model.DynamicOptions = ReadOptions(b, start, dynamicValues, dynamicCount, version);
            model.StaticKeyLength = staticKeyLength;
            model.KeyLength = staticKeyLength + dynamicKeyLength;
            model.KeyTable = new int[model.KeyLength * programCount];
            for (int i = 0; i < model.KeyTable.Length; i++)
            {
                model.KeyTable[i] = (int)U32(b, start + (long)keyTable + i * 4);
            }

            int programSize = version >= 8 ? 64 : version >= 5 ? 56 : 48;
            int variationField = version >= 8 ? 0x20 : version >= 7 ? 0x18 : 0x10;
            model.Programs = new ProgramKey?[programCount];
            for (int i = 0; i < programCount; i++)
            {
                long record = start + (long)programArray + (long)i * programSize;
                long variation = start + (long)U64(b, record + variationField);
                int bnshStart = checked(start + (int)bnsh);
                int program = checked(bnshStart + (int)U64(b, variation + 0x10));
                model.Programs[i] = finder.ReadProgram(b, bnshStart, program, register: false);
                if (model.Programs[i] == null)
                {
                    finder.Count($"bfsha v{version} programs that could not be read");
                }
                else if (!finder.Programs.ContainsKey(model.Programs[i].Value))
                {
                    finder.Count($"bfsha v{version} programs not among the BNSH programs");
                }
            }

            models.Add(model);
        }

        return models;
    }

    private static ShaderOptionInfo[] ReadOptions(byte[] b, int start, ulong values, int count, int version)
    {
        ShaderOptionInfo[] options = new ShaderOptionInfo[count];
        Cursor c = new(b, start + (long)values);

        for (int i = 0; i < count; i++)
        {
            ShaderOptionInfo option = new() { Name = Str(b, start, c.U64()) };
            ulong choiceDict = c.U64();
            c.U64(); // choice values

            if (version >= 9)
            {
                c.U16();
                option.DefaultChoice = c.I16();
                c.U16();
                c.U8();
                option.KeyOffset = c.U8();
                option.Bit32Mask = c.U32();
                option.Bit32Index = c.U8();
                option.Bit32Shift = c.U8();
                c.U16();
            }
            else
            {
                c.U8(); // choice count
                option.DefaultChoice = c.U8();
                c.U8();
                c.U16(); // uniform block offset
                option.KeyOffset = c.U8();
                option.Bit32Index = c.U8();
                option.Bit32Shift = c.U8();
                option.Bit32Mask = c.U32();
                c.U32();
            }

            option.Choices = DictKeys(b, start, choiceDict);
            options[i] = option;
        }

        return options;
    }

    /// <summary>Returns the number of materials read, or -1 if this isn't a BFRES file this reader understands.</summary>
    private static int ReadBfres(ShaderFinder finder, byte[] b, int start)
    {
        if (!b.AsSpan(start + 4, 4).SequenceEqual("    "u8) || U16(b, start + 0x0C) != 0xFEFF)
        {
            return -1;
        }

        uint fileSize = U32(b, start + 0x1C);
        if (fileSize < 0x100 || start + fileSize > b.Length)
        {
            return -1;
        }

        finder.Count($"bfres version {b[start + 0x0A]}.{b[start + 0x09]}");
        int version = b[start + 0x0A];
        if (version >= 10)
        {
            finder.Count("bfres unsupported version");
            return 0;
        }

        int limit = (int)(start + fileSize);
        int read = 0;
        HashSet<int> seen = new();

        // Version 9 shortened the block header in front of models, materials and shapes from 16 bytes to 8,
        // and dropped the signature of name dictionaries.
        int header = version >= 9 ? 8 : 16;
        int shapeMaterialIndex = version >= 9 ? 0x52 : version >= 4 ? 0x5E : 0x5A;
        int shapeSkinCount = version >= 9 ? 0x5A : version >= 4 ? 0x66 : 0x62;

        // Each model lists its materials and its shapes (meshes); a shape names its material by index and says
        // how many bone weights its vertices carry, which some shaders are specialized for.
        foreach (int fmdl in Find(b, "FMDL"u8, start + 1, limit))
        {
            try
            {
                ulong shapeValues = U64(b, fmdl + header + 0x20);
                ulong materialValues = U64(b, fmdl + header + 0x30);
                int shapeCount = U16(b, fmdl + 0x6A);
                int materialCount = U16(b, fmdl + 0x6C);
                if (materialValues == 0 || materialValues >= fileSize || shapeValues >= fileSize)
                {
                    continue;
                }

                List<MaterialInfo> materials = new();
                foreach (int fmat in Find(b, "FMAT"u8, (int)(start + (long)materialValues), limit).Take(materialCount))
                {
                    MaterialInfo material = ReadMaterial(b, start, fmat, header, fileSize);
                    materials.Add(material);
                    seen.Add(fmat);
                }

                if (shapeValues != 0)
                {
                    foreach (int fshp in Find(b, "FSHP"u8, (int)(start + (long)shapeValues), limit).Take(shapeCount))
                    {
                        int materialIndex = U16(b, fshp + shapeMaterialIndex);
                        int skinCount = b[fshp + shapeSkinCount];
                        if (materialIndex < materials.Count && materials[materialIndex] != null)
                        {
                            materials[materialIndex].SkinCounts.Add(skinCount);
                        }
                    }
                }

                foreach (MaterialInfo material in materials.Where(m => m != null))
                {
                    finder.Materials.Add(material);
                    read++;
                }
            }
            catch (Exception)
            {
                finder.Count("bfres models that could not be read");
            }
        }

        // Materials outside any model that was read.
        foreach (int fmat in Find(b, "FMAT"u8, start + 1, limit).Where(p => !seen.Contains(p)))
        {
            MaterialInfo material = ReadMaterial(b, start, fmat, header, fileSize);
            if (material != null)
            {
                finder.Materials.Add(material);
                read++;
            }
        }

        return read;
    }

    private static MaterialInfo ReadMaterial(byte[] b, int start, int fmat, int header, uint fileSize)
    {
        try
        {
            ulong assign = U64(b, fmat + header + 0x18);
            if (assign == 0 || assign >= fileSize)
            {
                return null;
            }

            long a = start + (long)assign;
            string archive = Str(b, start, U64(b, a));
            string model = Str(b, start, U64(b, a + 0x08));
            ulong optionValues = U64(b, a + 0x30);
            ulong optionDict = U64(b, a + 0x38);
            string[] names = DictKeys(b, start, optionDict);

            Dictionary<string, string> options = new(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                options[names[i]] = Str(b, start, U64(b, start + (long)optionValues + i * 8));
            }

            // Render info: name, data offset, value count, type (0 int, 1 float, 2 string), 24 bytes each.
            Dictionary<string, string> renderInfo = new();
            ulong renderValues = U64(b, fmat + header + 0x08);
            string[] renderNames = DictKeys(b, start, U64(b, fmat + header + 0x10));
            for (int i = 0; i < renderNames.Length && renderValues != 0; i++)
            {
                long entry = start + (long)renderValues + i * 24;
                long data = start + (long)U64(b, entry + 8);
                int count = Math.Min((int)U16(b, entry + 0x10), 16);
                int type = b[entry + 0x12];
                IEnumerable<string> values = type switch
                {
                    0 => Enumerable.Range(0, count).Select(k => ((int)U32(b, data + k * 4)).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    1 => Enumerable.Range(0, count).Select(k => BitConverter.UInt32BitsToSingle(U32(b, data + k * 4)).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    _ => Enumerable.Range(0, data == start ? 0 : count).Select(k => Str(b, start, U64(b, data + k * 8))),
                };
                renderInfo[renderNames[i]] = string.Join(",", values);
            }

            return archive != null && model != null ? new MaterialInfo { Archive = archive, Model = model, Options = options, RenderInfo = renderInfo } : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Sorts the programs found in the game files by whether some material can select them.
/// </summary>
sealed class MaterialUsage
{
    public const string Exact = "picked by a material";
    public const string Unreferenced = "in an archive, no material picks it";
    public const string Other = "not in a material archive";

    public Dictionary<ProgramKey, string> ClassOf { get; } = new();
    public List<string> Report { get; } = new();

    /// <summary>Per shading model: the distinct option choices materials ask for (unset options at their default).</summary>
    public Dictionary<ShadingModelInfo, List<int[]>> Requests { get; } = new();

    /// <summary>The material behind each entry of <see cref="Requests"/>.</summary>
    public Dictionary<ShadingModelInfo, List<MaterialInfo>> RequestMaterials { get; } = new();

    public static MaterialUsage Analyze(ShaderFinder finder)
    {
        MaterialUsage usage = new();
        List<ShadingModelInfo> models = finder.ShadingModels.ToList();
        List<MaterialInfo> materials = finder.Materials.ToList();

        HashSet<ProgramKey> inArchive = new();
        foreach (ShadingModelInfo model in models)
        {
            foreach (ProgramKey? key in model.Programs)
            {
                if (key.HasValue)
                {
                    inArchive.Add(key.Value);
                }
            }
        }

        Dictionary<(string, string), List<ShadingModelInfo>> byName = models
            .GroupBy(m => (m.Archive, m.Name))
            .ToDictionary(g => g.Key, g => g.ToList());

        HashSet<ProgramKey> exact = new();
        Dictionary<string, int> unmatched = new();
        int unknownChoice = 0;
        HashSet<string> usedModels = new();

        // Many materials share the same settings, so each distinct request is resolved once.
        foreach (var group in materials.GroupBy(m => m.Archive + "|" + m.Model + "|" + string.Join(";", m.Options.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Key + "=" + o.Value)) + "|" + string.Join(",", m.SkinCounts.Order()) + "|" + string.Join(";", m.RenderInfo.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => o.Key + "=" + o.Value))))
        {
            MaterialInfo material = group.First();
            if (!byName.TryGetValue((material.Archive, material.Model), out List<ShadingModelInfo> candidates))
            {
                string key = $"{material.Archive}/{material.Model}";
                unmatched[key] = unmatched.GetValueOrDefault(key) + group.Count();
                continue;
            }

            usedModels.Add($"{material.Archive}/{material.Model}");
            foreach (ShadingModelInfo model in candidates)
            {
                int[] choices = new int[model.StaticOptions.Length];
                for (int o = 0; o < choices.Length; o++)
                {
                    ShaderOptionInfo option = model.StaticOptions[o];
                    if (material.Options.TryGetValue(option.Name, out string value))
                    {
                        choices[o] = Array.IndexOf(option.Choices, value);
                        unknownChoice += choices[o] < 0 ? 1 : 0;
                    }
                    else
                    {
                        choices[o] = option.DefaultChoice;
                    }
                }

                // The number of bone weights per vertex comes from the meshes drawn with the material, whatever
                // the material itself says.
                List<int[]> requests = new() { choices };
                int skin = Array.FindIndex(model.StaticOptions, o => o.Name.Contains("skin", StringComparison.OrdinalIgnoreCase));
                if (skin >= 0 && material.SkinCounts.Count > 0)
                {
                    requests.Clear();
                    foreach (int count in material.SkinCounts)
                    {
                        int[] copy = (int[])choices.Clone();
                        int choice = Array.IndexOf(model.StaticOptions[skin].Choices, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        copy[skin] = choice >= 0 ? choice : copy[skin];
                        requests.Add(copy);
                    }
                }

                if (!usage.Requests.TryGetValue(model, out List<int[]> all))
                {
                    usage.Requests[model] = all = new();
                    usage.RequestMaterials[model] = new();
                }

                all.AddRange(requests);
                usage.RequestMaterials[model].AddRange(requests.Select(_ => material));
            }
        }

        bool[] none = Array.Empty<bool>();
        foreach ((ShadingModelInfo model, List<int[]> requests) in usage.Requests)
        {
            none = new bool[model.StaticOptions.Length];
            HashSet<ulong> wanted = requests.Select(r => ChoiceHash(r, none)).ToHashSet();
            for (int p = 0; p < model.Programs.Length; p++)
            {
                if (model.Programs[p].HasValue && wanted.Contains(ChoiceHash(model.ChoicesOf(p), none)))
                {
                    exact.Add(model.Programs[p].Value);
                }
            }
        }

        foreach (ProgramKey key in finder.Programs.Keys)
        {
            usage.ClassOf[key] = exact.Contains(key) ? Exact
                : inArchive.Contains(key) ? Unreferenced
                : Other;
        }

        usage.Report.Add($"Shader archives: {models.Select(m => m.Archive).Distinct().Count()} archives, {models.Count} shading models, {models.Sum(m => m.Programs.Length)} programs; materials: {materials.Count} ({usedModels.Count} shading models used, {materials.Count(m => m.SkinCounts.Count > 0)} with mesh skin counts)");
        usage.Report.Add("  materials asking for a shading model that wasn't found: " + (unmatched.Count == 0 ? "none" : string.Join(", ", unmatched.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} x{kv.Value}"))));
        usage.Report.Add($"  material option values not among the archive's choices: {unknownChoice}");
        usage.Report.Add("  shading models no material uses: " + string.Join(", ", models.Select(m => $"{m.Archive}/{m.Name}").Distinct().Where(n => !usedModels.Contains(n)).Take(12)));
        return usage;
    }

    /// <summary>Hash of a program's option choices, leaving out the options marked in <paramref name="free"/>.</summary>
    public static ulong ChoiceHash(int[] choices, bool[] free)
    {
        ulong hash = 14695981039346656037UL;
        for (int o = 0; o < choices.Length; o++)
        {
            hash = (hash ^ (uint)(free[o] ? -1 : choices[o])) * 1099511628211UL;
        }

        return hash;
    }
}

/// <summary>
/// Matches programs found in the game files to programs in a Ryubing cache, by the guest code of each stage.
/// </summary>
static class ProgramSignatures
{
    /// <summary>The guest code identity of each found program's stages, in Ryubing's stage order.</summary>
    public static Dictionary<ProgramKey, ulong[]> StageIds(ShaderFinder finder, Dictionary<ulong, StageProbe> probes)
    {
        Dictionary<ulong, ulong> guestOfBlob = new();
        foreach (StageBlob blob in finder.Blobs.Values)
        {
            if (probes.TryGetValue(blob.Id, out StageProbe probe) && probe.Error == null)
            {
                StageCode code = Prober.GuestCodeOf(blob, probe);
                guestOfBlob[blob.Id] = RyubingCache.IdOf(code.Code, code.Cb1);
            }
        }

        Dictionary<ProgramKey, ulong[]> stageIds = new();
        foreach (ProgramKey key in finder.Programs.Keys)
        {
            ulong[] ids = new ulong[6];
            bool translated = true;
            for (int bnshStage = 0; bnshStage < 6 && translated; bnshStage++)
            {
                if (key[bnshStage] != 0)
                {
                    translated = guestOfBlob.TryGetValue(key[bnshStage], out ulong guest);
                    ids[key.Compute != 0 ? 0 : bnshStage + 1] = guest;
                }
            }

            if (translated)
            {
                stageIds[key] = ids;
            }
        }

        return stageIds;
    }

    public static Dictionary<ProgramKey, string> Compute(Dictionary<ProgramKey, ulong[]> stageIds) =>
        stageIds.ToDictionary(kv => kv.Key, kv => string.Join(",", kv.Value));

    public static string Of(RyubingCache cache, CacheProgram program)
    {
        ulong[] ids = new ulong[6];
        for (int stage = 0; stage < 6; stage++)
        {
            if (program.GuestIndex[stage] >= 0)
            {
                ids[stage] = cache.Guest[program.GuestIndex[stage]].Id;
            }
        }

        return string.Join(",", ids);
    }

    /// <summary>The found programs the game used, going by the given cache programs.</summary>
    public static HashSet<ProgramKey> Used(RyubingCache cache, IEnumerable<CacheProgram> programs, Dictionary<ProgramKey, string> signatureOf)
    {
        HashSet<string> signatures = programs.Select(p => Of(cache, p)).ToHashSet();
        return signatureOf.Where(kv => signatures.Contains(kv.Value)).Select(kv => kv.Key).ToHashSet();
    }

}

/// <summary>What the game files and the player's cache say about one program.</summary>
[Flags]
enum Evidence
{
    None = 0,

    /// <summary>It's in a material archive (a BFSHA shading model).</summary>
    InArchive = 1,

    /// <summary>Its shading model is used by some model material.</summary>
    ModelUsed = 2,

    /// <summary>A material picks its static options exactly.</summary>
    Picked = 4,

    /// <summary>A material picks it once options the game sets itself take values seen in the player's cache.</summary>
    PickedWithGameValues = 8,

    /// <summary>The values of its dynamic options (chosen while drawing) were all seen in the player's cache.</summary>
    DynamicSeen = 16,

    /// <summary>Its shader file holds a program the player's cache shows the game used.</summary>
    FileUsed = 32,

    /// <summary>One of its stages has the same code as a stage of a program the game used.</summary>
    SharesCode = 64,
}

/// <summary>
/// Ranks programs by how likely the game is to use them, from its model materials and the programs the player's
/// own cache shows it used. Materials name the shader variants they need, but the game also sets some options
/// itself while drawing (render pass, alpha test, skinning...), and those are only known from real play.
/// </summary>
sealed class BuildPriority
{
    /// <summary>A material picks it, and the options the game sets match values seen in the player's cache.</summary>
    public const int Likely = 0;

    /// <summary>A material picks it with other values for options the game sets, or it isn't in a material archive.</summary>
    public const int Possible = 1;

    /// <summary>In a material archive, but no material picks it.</summary>
    public const int Unlikely = 2;

    public Dictionary<ProgramKey, Evidence> EvidenceOf { get; } = new();
    public List<string> GameSetOptions { get; } = new();

    /// <summary>How the tiers are worked out from the evidence.</summary>
    public Func<Evidence, int> Policy { get; init; } = DefaultPolicy;

    public int Rank(ProgramKey key) => EvidenceOf.TryGetValue(key, out Evidence evidence) ? Policy(evidence) : Possible;

    public static int DefaultPolicy(Evidence e) =>
        !e.HasFlag(Evidence.InArchive) ? Likely
        : !e.HasFlag(Evidence.ModelUsed) ? Possible
        : e.HasFlag(Evidence.PickedWithGameValues) && e.HasFlag(Evidence.DynamicSeen) ? Likely
        : e.HasFlag(Evidence.PickedWithGameValues) || e.HasFlag(Evidence.Picked) ? Possible
        : Unlikely;

    /// <summary>
    /// The order to use when the player's cache is too small to learn from: dynamic option values the game used
    /// are the strongest sign in every game measured, then a material picking the program (models no material uses
    /// are drawn by game code, so materials say nothing about them), then sharing code with a program the game used.
    /// </summary>
    public static int FixedPolicy(Evidence e) => SignalTier(e) * 2 + (e.HasFlag(Evidence.SharesCode) ? 0 : 1);

    public static int SignalTier(Evidence e) =>
        !e.HasFlag(Evidence.InArchive) ? 0
        : !e.HasFlag(Evidence.DynamicSeen) ? 2
        : e.HasFlag(Evidence.ModelUsed) && !e.HasFlag(Evidence.Picked) && !e.HasFlag(Evidence.PickedWithGameValues) ? 1
        : 0;

    public BuildPriority WithPolicy(Func<Evidence, int> policy)
    {
        BuildPriority copy = new() { Policy = policy };
        foreach ((ProgramKey key, Evidence evidence) in EvidenceOf)
        {
            copy.EvidenceOf[key] = evidence;
        }

        copy.GameSetOptions.AddRange(GameSetOptions);
        return copy;
    }

    public static BuildPriority Compute(ShaderFinder finder, MaterialUsage usage, HashSet<ProgramKey> learned, Dictionary<ProgramKey, ulong[]> stageIds)
    {
        BuildPriority priority = new();
        List<ShadingModelInfo> models = finder.ShadingModels.ToList();
        static string NameOf(ShadingModelInfo m) => m.Archive + "/" + m.Name;

        // Learned programs, by shading model name (archives can repeat a model with a different set of programs).
        Dictionary<string, List<(ShadingModelInfo Model, int Index)>> learnedByName = new();
        foreach (ShadingModelInfo model in models)
        {
            for (int p = 0; p < model.Programs.Length; p++)
            {
                if (model.Programs[p].HasValue && learned.Contains(model.Programs[p].Value))
                {
                    if (!learnedByName.TryGetValue(NameOf(model), out var list))
                    {
                        learnedByName[NameOf(model)] = list = new();
                    }

                    list.Add((model, p));
                }
            }
        }

        // Options the game sets: those that tell a learned program apart from the nearest material request.
        Dictionary<string, Dictionary<string, HashSet<string>>> engineValues = new();
        Dictionary<string, HashSet<string>> dynamicValues = new();
        foreach ((string name, var programs) in learnedByName)
        {
            Dictionary<string, HashSet<string>> values = engineValues[name] = new();
            foreach ((ShadingModelInfo model, int p) in programs)
            {
                int[] choices = model.ChoicesOf(p);
                int[] nearest = Nearest(usage.Requests.GetValueOrDefault(model), choices, out int distance);
                if (nearest != null && distance is > 0 and <= 4)
                {
                    for (int o = 0; o < choices.Length; o++)
                    {
                        if (choices[o] != nearest[o])
                        {
                            values.TryAdd(model.StaticOptions[o].Name, new HashSet<string>());
                        }
                    }
                }

                int[] dynamic = model.DynamicChoicesOf(p);
                for (int d = 0; d < dynamic.Length; d++)
                {
                    Add(dynamicValues, model.DynamicOptions[d].Name, model.DynamicOptions[d].Choices.ElementAtOrDefault(dynamic[d]));
                }
            }

            // The values learned programs use for those options.
            foreach ((ShadingModelInfo model, int p) in programs)
            {
                int[] choices = model.ChoicesOf(p);
                for (int o = 0; o < choices.Length; o++)
                {
                    if (values.TryGetValue(model.StaticOptions[o].Name, out HashSet<string> seen))
                    {
                        seen.Add(model.StaticOptions[o].Choices.ElementAtOrDefault(choices[o]) ?? "?");
                    }
                }
            }

            if (values.Count > 0)
            {
                priority.GameSetOptions.Add($"{name}: " + string.Join(", ", values.Select(kv => $"{kv.Key}={string.Join("/", kv.Value)}")));
            }
        }

        HashSet<ulong> learnedContainers = learned.Where(finder.Programs.ContainsKey).SelectMany(k => finder.Programs[k].ContainerIds).ToHashSet();
        HashSet<ulong> learnedCode = learned.Where(stageIds.ContainsKey).SelectMany(k => stageIds[k]).Where(id => id != 0).ToHashSet();
        Evidence FileEvidence(ProgramKey key) =>
            (finder.Programs.TryGetValue(key, out FoundProgram found) && found.SharesContainerWith(learnedContainers) ? Evidence.FileUsed : Evidence.None)
            | (stageIds.TryGetValue(key, out ulong[] ids) && ids.Any(id => id != 0 && learnedCode.Contains(id)) ? Evidence.SharesCode : Evidence.None);

        foreach (ShadingModelInfo model in models)
        {
            List<int[]> requests = usage.Requests.GetValueOrDefault(model) ?? new();
            Dictionary<string, HashSet<string>> values = engineValues.GetValueOrDefault(NameOf(model)) ?? new();
            bool[] none = new bool[model.StaticOptions.Length];
            bool[] free = model.StaticOptions.Select(o => values.ContainsKey(o.Name)).ToArray();
            HashSet<ulong> exact = requests.Select(r => MaterialUsage.ChoiceHash(r, none)).ToHashSet();
            HashSet<ulong> loose = requests.Select(r => MaterialUsage.ChoiceHash(r, free)).ToHashSet();

            for (int p = 0; p < model.Programs.Length; p++)
            {
                if (!model.Programs[p].HasValue)
                {
                    continue;
                }

                ProgramKey key = model.Programs[p].Value;
                Evidence evidence = Evidence.InArchive | FileEvidence(key);

                bool dynamicSeen = true;
                int[] dynamic = model.DynamicChoicesOf(p);
                for (int d = 0; d < dynamic.Length && dynamicSeen; d++)
                {
                    HashSet<string> seen = dynamicValues.GetValueOrDefault(model.DynamicOptions[d].Name);
                    dynamicSeen = seen == null || seen.Contains(model.DynamicOptions[d].Choices.ElementAtOrDefault(dynamic[d]) ?? "?");
                }

                evidence |= dynamicSeen ? Evidence.DynamicSeen : Evidence.None;

                // Shading models no material uses (effects, decals, engine passes) are drawn by game code, so the
                // model files say nothing about them.
                if (requests.Count > 0)
                {
                    int[] choices = model.ChoicesOf(p);
                    bool learnedValues = true;
                    for (int o = 0; o < choices.Length && learnedValues; o++)
                    {
                        learnedValues = !free[o] || values[model.StaticOptions[o].Name].Contains(model.StaticOptions[o].Choices.ElementAtOrDefault(choices[o]) ?? "?");
                    }

                    evidence |= Evidence.ModelUsed;
                    evidence |= exact.Contains(MaterialUsage.ChoiceHash(choices, none)) ? Evidence.Picked : Evidence.None;
                    evidence |= learnedValues && loose.Contains(MaterialUsage.ChoiceHash(choices, free)) ? Evidence.PickedWithGameValues : Evidence.None;
                }

                // A program can sit in several archives; keep what any of them says for it.
                priority.EvidenceOf[key] = priority.EvidenceOf.GetValueOrDefault(key) | evidence;
            }
        }

        // Programs outside material archives (effects, UI, system shaders).
        foreach (ProgramKey key in finder.Programs.Keys)
        {
            if (!priority.EvidenceOf.ContainsKey(key))
            {
                priority.EvidenceOf[key] = FileEvidence(key);
            }
        }

        return priority;
    }

    private static int[] Nearest(List<int[]> requests, int[] choices, out int best)
    {
        best = int.MaxValue;
        int[] nearest = null;
        foreach (int[] request in requests ?? new List<int[]>())
        {
            int distance = 0;
            for (int o = 0; o < choices.Length; o++)
            {
                distance += choices[o] != request[o] ? 1 : 0;
            }

            if (distance < best || distance == best && Compare(request, nearest) < 0)
            {
                best = distance;
                nearest = request;
            }
        }

        return nearest;
    }

    private static int Compare(int[] left, int[] right)
    {
        if (right == null)
        {
            return -1;
        }

        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            int compared = left[i].CompareTo(right[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out HashSet<string> set))
        {
            map[key] = set = new();
        }

        set.Add(value ?? "?");
    }
}

/// <summary>
/// Checks the material classes against a real shader cache: which programs the game really compiled, and
/// whether they were ones a material picks.
/// </summary>
static class UsageReport
{
    private static readonly string[] Order = { MaterialUsage.Exact, MaterialUsage.Unreferenced, MaterialUsage.Other };

    public static string Run(RyubingCache full, ShaderFinder finder, Dictionary<ulong, StageProbe> probes, MaterialUsage usage, Func<int, Func<ProgramKey, int>, Plan> plan)
    {
        StringBuilder sb = new();
        usage.Report.ForEach(line => sb.AppendLine(line));

        // Programs are matched to the real cache by the guest code Ryubing stores for each stage.
        Dictionary<ProgramKey, ulong[]> stageIds = ProgramSignatures.StageIds(finder, probes);
        Dictionary<ProgramKey, string> signatureOf = ProgramSignatures.Compute(stageIds);
        Dictionary<string, List<ProgramKey>> bySignature = signatureOf.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());

        Dictionary<string, int> realPrograms = new();
        HashSet<string> realPairs = new();
        HashSet<ProgramKey> used = new();
        foreach (CacheProgram program in full.Programs)
        {
            string signature = ProgramSignatures.Of(full, program);
            string cls = "not in the game's shader containers";
            if (bySignature.TryGetValue(signature, out List<ProgramKey> keys))
            {
                cls = keys.Select(k => usage.ClassOf[k]).OrderBy(c => Array.IndexOf(Order, c)).First();
                keys.ForEach(k => used.Add(k));
            }

            realPrograms[cls] = realPrograms.GetValueOrDefault(cls) + 1;
            if (realPairs.Add(signature))
            {
                realPrograms[cls + " (distinct code)"] = realPrograms.GetValueOrDefault(cls + " (distinct code)") + 1;
            }
        }

        HashSet<ProgramKey> planned = plan(200, null).Programs.Select(p => p.Source.Key).ToHashSet();
        sb.AppendLine($"Real cache: {full.Programs.Count} programs, {realPairs.Count} distinct code combinations.");
        sb.AppendLine("Programs in the game files, by class (used = compiled in the real cache):");
        foreach (string cls in Order)
        {
            List<ProgramKey> inClass = usage.ClassOf.Where(kv => kv.Value == cls && signatureOf.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
            int usedCount = inClass.Count(used.Contains);
            int plannedCount = inClass.Count(planned.Contains);
            int plannedUsed = inClass.Count(k => planned.Contains(k) && used.Contains(k));
            sb.AppendLine($"  {cls}: {inClass.Count} programs, {usedCount} used ({Pct(usedCount, inClass.Count)}); built today: {plannedCount}, of which used {plannedUsed} ({Pct(plannedUsed, plannedCount)})");
        }

        sb.AppendLine("Where the real cache's programs come from:");
        foreach (string cls in Order.Append("not in the game's shader containers"))
        {
            int count = realPrograms.GetValueOrDefault(cls);
            int distinct = realPrograms.GetValueOrDefault(cls + " (distinct code)");
            sb.AppendLine($"  {cls}: {count} programs ({Pct(count, full.Programs.Count)}), {distinct} distinct code combinations");
        }

        ShadingModelBreakdown(sb, finder, usage, used);
        CompareOrders(sb, full, finder, usage, stageIds, signatureOf, learnCount => rank => plan(learnCount, rank), 200, evidenceTable: true);
        CompareOrders(sb, full, finder, usage, stageIds, signatureOf, learnCount => rank => plan(learnCount, rank), 50, evidenceTable: false);
        CompareOrders(sb, full, finder, usage, stageIds, signatureOf, learnCount => rank => plan(learnCount, rank), 1000, evidenceTable: false);
        return sb.ToString();
    }

    /// <summary>
    /// Simulates a new player whose cache holds only the first <paramref name="learnCount"/> programs, and compares
    /// build orders by how much of what the game compiled later each one includes within the first N entries.
    /// </summary>
    private static void CompareOrders(StringBuilder sb, RyubingCache full, ShaderFinder finder, MaterialUsage usage, Dictionary<ProgramKey, ulong[]> stageIds,
        Dictionary<ProgramKey, string> signatureOf, Func<int, Func<Func<ProgramKey, int>, Plan>> planFor, int learnCount, bool evidenceTable)
    {
        Func<Func<ProgramKey, int>, Plan> plan = planFor(learnCount);
        HashSet<ProgramKey> learned = ProgramSignatures.Used(full, full.Programs.Take(learnCount), signatureOf);
        HashSet<ProgramKey> usedLater = ProgramSignatures.Used(full, full.Programs.Skip(learnCount), signatureOf);
        usedLater.ExceptWith(learned);

        BuildPriority priority = BuildPriority.Compute(finder, usage, learned, stageIds);
        List<PlannedProgram> today = plan(null).Programs;

        // How often each combination of evidence is used later; ordering by that is the best any rule using only
        // this evidence could do.
        var cells = today.Select(p => p.Source.Key).Distinct()
            .GroupBy(k => priority.EvidenceOf.GetValueOrDefault(k))
            .Select(g => (Evidence: g.Key, Programs: g.Count(), Used: g.Count(usedLater.Contains)))
            .OrderByDescending(c => (double)c.Used / c.Programs)
            .ToList();
        Dictionary<Evidence, int> bestRank = cells.Select((c, i) => (c.Evidence, i)).ToDictionary(x => x.Evidence, x => x.i);

        sb.AppendLine($"Evidence, learning from the first {learnCount} programs (A in a material archive, M model used by a material, P picked, G picked with game-set values, D dynamic values seen, F file used, L shares code with a used program):");
        foreach ((Evidence evidence, int programs, int used) in evidenceTable ? cells : new())
        {
            sb.AppendLine($"  {Code(evidence),-7} {programs,7} programs, {used,6} used later ({Pct(used, programs)})");
        }

        var orders = new (string Name, Func<ProgramKey, int> Rank)[]
        {
            ("today", null),
            ("materials", priority.Rank),
            ("fixed signals", priority.WithPolicy(BuildPriority.FixedPolicy).Rank),
            ("best possible from this evidence", priority.WithPolicy(e => bestRank.GetValueOrDefault(e, cells.Count)).Rank),
        };

        sb.AppendLine($"Build order, learning from the first {learnCount} programs (share of programs used later that are among the first N entries):");
        foreach ((string name, Func<ProgramKey, int> rank) in orders)
        {
            List<PlannedProgram> programs = rank == null ? today : plan(rank).Programs;
            int buildable = programs.Count(p => usedLater.Contains(p.Source.Key));
            List<string> points = new();
            foreach (int cap in new[] { 2000, 5000, 10000, 20000, 30000, 40000 }.Where(c => c < programs.Count))
            {
                int covered = programs.Take(cap).Count(p => usedLater.Contains(p.Source.Key));
                points.Add($"{cap / 1000}k: {Pct(covered, buildable)}");
            }

            sb.AppendLine($"  {name,-34} {string.Join(", ", points)} (all {programs.Count}: 100%)");
        }

        sb.AppendLine("  game-set options learned: " + string.Join(" | ", priority.GameSetOptions.Take(4)));
    }

    private static string Code(Evidence e) =>
        (e.HasFlag(Evidence.InArchive) ? "A" : "-") + (e.HasFlag(Evidence.ModelUsed) ? "M" : "-") + (e.HasFlag(Evidence.Picked) ? "P" : "-")
        + (e.HasFlag(Evidence.PickedWithGameValues) ? "G" : "-") + (e.HasFlag(Evidence.DynamicSeen) ? "D" : "-") + (e.HasFlag(Evidence.FileUsed) ? "F" : "-")
        + (e.HasFlag(Evidence.SharesCode) ? "L" : "-");

    private static ulong Hash(int[] choices, bool[] free) => MaterialUsage.ChoiceHash(choices, free);

    /// <summary>
    /// Per shading model: how many programs materials pick and how many the game used. For used programs no
    /// material picks, finds the static options that differ from the nearest material request (options the game
    /// must set itself), then tries treating the most common ones as free.
    /// </summary>
    private static void ShadingModelBreakdown(StringBuilder sb, ShaderFinder finder, MaterialUsage usage, HashSet<ProgramKey> used)
    {
        sb.AppendLine("Per shading model (programs / picked by a material / used / used and picked):");
        foreach (ShadingModelInfo model in finder.ShadingModels.DistinctBy(m => (m.Archive, m.Name, m.Programs.Length)))
        {
            int optionCount = model.StaticOptions.Length;
            int[][] programs = Enumerable.Range(0, model.Programs.Length).Select(model.ChoicesOf).ToArray();
            bool[] isUsed = model.Programs.Select(k => k.HasValue && used.Contains(k.Value)).ToArray();
            List<int[]> requests = usage.Requests.GetValueOrDefault(model) ?? new();

            bool[] none = new bool[optionCount];
            List<MaterialInfo> requestMaterials = usage.RequestMaterials.GetValueOrDefault(model) ?? new();
            List<(int[] Choices, MaterialInfo Material)> distinctRequests = requests.Zip(requestMaterials).DistinctBy(r => Hash(r.First, none)).ToList();
            requests = distinctRequests.Select(r => r.Choices).ToList();
            HashSet<ulong> requested = requests.Select(r => Hash(r, none)).ToHashSet();
            int picked = 0, usedCount = 0, usedPicked = 0;
            List<int> usedNotPicked = new();

            for (int p = 0; p < programs.Length; p++)
            {
                bool isPicked = requested.Contains(Hash(programs[p], none));
                picked += isPicked ? 1 : 0;
                usedCount += isUsed[p] ? 1 : 0;
                usedPicked += isUsed[p] && isPicked ? 1 : 0;
                if (isUsed[p] && !isPicked)
                {
                    usedNotPicked.Add(p);
                }
            }

            sb.AppendLine($"  {model.Archive}/{model.Name}: {programs.Length} / {picked} / {usedCount} / {usedPicked}  ({optionCount} static options, {requests.Count} material requests)");

            // Dynamic options are chosen while drawing, so every material can use any of their values; see which
            // values the game really used among the programs materials pick.
            if (model.DynamicOptions.Length > 0 && picked > 0)
            {
                List<string> parts = new();
                for (int d = 0; d < model.DynamicOptions.Length; d++)
                {
                    ShaderOptionInfo option = model.DynamicOptions[d];
                    Dictionary<int, (int Picked, int Used)> byValue = new();
                    for (int p = 0; p < programs.Length; p++)
                    {
                        if (!requested.Contains(Hash(programs[p], none)))
                        {
                            continue;
                        }

                        int value = model.DynamicChoicesOf(p)[d];
                        (int pickedCount, int usedValue) = byValue.GetValueOrDefault(value);
                        byValue[value] = (pickedCount + 1, usedValue + (isUsed[p] ? 1 : 0));
                    }

                    if (byValue.Count > 1)
                    {
                        parts.Add($"{option.Name}: " + string.Join(", ", byValue.OrderBy(kv => kv.Key).Select(kv => $"{option.Choices.ElementAtOrDefault(kv.Key)}={kv.Value.Used}/{kv.Value.Picked}")));
                    }
                }

                sb.AppendLine("    dynamic options among picked programs (used/picked per value): " + (parts.Count == 0 ? "one value each" : string.Join(" | ", parts)));
            }

            if (requests.Count == 0 || usedNotPicked.Count == 0)
            {
                continue;
            }

            // Which options separate each used-but-unpicked program from the nearest request.
            Dictionary<string, int> differing = new();
            Dictionary<int, int> distances = new();
            HashSet<int> candidates = new();
            Dictionary<string, int> nearestRenderInfo = new();
            foreach (int p in usedNotPicked)
            {
                int[] choices = programs[p];
                int best = int.MaxValue;
                int bestIndex = -1;
                for (int r = 0; r < requests.Count; r++)
                {
                    int[] request = requests[r];
                    int distance = 0;
                    for (int o = 0; o < optionCount && distance < best; o++)
                    {
                        distance += choices[o] != request[o] ? 1 : 0;
                    }

                    if (distance < best)
                    {
                        best = distance;
                        bestIndex = r;
                    }
                }

                int[] bestRequest = requests[bestIndex];
                foreach ((string key, string value) in distinctRequests[bestIndex].Material.RenderInfo)
                {
                    nearestRenderInfo[key + "=" + value] = nearestRenderInfo.GetValueOrDefault(key + "=" + value) + 1;
                }

                List<int> differ = Enumerable.Range(0, optionCount).Where(o => choices[o] != bestRequest[o]).ToList();
                differ.ForEach(o => candidates.Add(o));
                distances[best] = distances.GetValueOrDefault(best) + 1;
                string names = string.Join("+", differ.Select(o => model.StaticOptions[o].Name));
                differing[names] = differing.GetValueOrDefault(names) + 1;
            }

            // Render settings that are much more common among those materials than among all materials.
            Dictionary<string, int> allRenderInfo = new();
            foreach ((int[] _, MaterialInfo material) in distinctRequests)
            {
                foreach ((string key, string value) in material.RenderInfo)
                {
                    allRenderInfo[key + "=" + value] = allRenderInfo.GetValueOrDefault(key + "=" + value) + 1;
                }
            }

            var lifted = nearestRenderInfo
                .Where(kv => kv.Value >= 20)
                .Select(kv => (Pair: kv.Key, Count: kv.Value, Lift: (kv.Value / (double)usedNotPicked.Count) / (allRenderInfo.GetValueOrDefault(kv.Key) / (double)distinctRequests.Count)))
                .OrderByDescending(x => x.Lift)
                .Take(8);
            sb.AppendLine($"    render info of the nearest material (count among {usedNotPicked.Count}, lift vs all {distinctRequests.Count} requests): " + string.Join(" | ", lifted.Select(x => $"{x.Pair} {x.Count} x{x.Lift:F1}")));

            sb.AppendLine("    distance to nearest material request: " + string.Join(", ", distances.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")));
            sb.AppendLine("    options that differ: " + string.Join(" | ", differing.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} x{kv.Value}")));
            foreach (int o in candidates.OrderByDescending(o => usedNotPicked.Count(p => programs[p][o] != model.StaticOptions[o].DefaultChoice)).Take(5))
            {
                ShaderOptionInfo option = model.StaticOptions[o];
                Dictionary<int, int> usedValues = usedNotPicked.GroupBy(p => programs[p][o]).ToDictionary(g => g.Key, g => g.Count());
                sb.AppendLine($"    '{option.Name}' choices [{string.Join(", ", option.Choices)}], default {option.Choices.ElementAtOrDefault(option.DefaultChoice)}; in used unpicked programs: {string.Join(", ", usedValues.OrderBy(kv => kv.Key).Select(kv => $"{option.Choices.ElementAtOrDefault(kv.Key)}x{kv.Value}"))}");
            }

            // Greedily treat options as set by the game, and see what that adds.
            bool[] free = new bool[optionCount];
            for (int step = 0; step < 8; step++)
            {
                int bestOption = -1, bestGain = 0, bestAdded = 0;
                foreach (int o in candidates.Where(o => !free[o]))
                {
                    free[o] = true;
                    HashSet<ulong> keys = requests.Select(r => Hash(r, free)).ToHashSet();
                    int added = 0, gain = 0;
                    for (int p = 0; p < programs.Length; p++)
                    {
                        if (keys.Contains(Hash(programs[p], free)))
                        {
                            added++;
                            gain += isUsed[p] ? 1 : 0;
                        }
                    }

                    free[o] = false;
                    if (gain > bestGain || (gain == bestGain && bestOption >= 0 && added < bestAdded))
                    {
                        bestOption = o;
                        bestGain = gain;
                        bestAdded = added;
                    }
                }

                if (bestOption < 0)
                {
                    break;
                }

                free[bestOption] = true;
                sb.AppendLine($"    + treat '{model.StaticOptions[bestOption].Name}' ({model.StaticOptions[bestOption].Choices.Length} choices) as set by the game: picks {bestAdded} programs, {bestGain} of {usedCount} used ones ({Pct(bestGain, bestAdded)} of picked are used)");
            }
        }
    }

    private static string Pct(int a, int b) => b == 0 ? "n/a" : $"{100.0 * a / b:F1}%";
}
