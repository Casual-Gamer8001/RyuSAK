namespace RyuSAK.ShaderBuilder;

/// <summary>
/// What RyuSAK learns about a game from the shaders Ryubing already cached for it: the GPU state the game uses
/// for every draw (depth mode, vertex formats and so on), so new cache entries can be filled in to match.
/// </summary>
sealed class Profile
{
    public int GraphicsPrograms;
    public int ComputePrograms;
    public int BuiltPrograms;
    public GfxState Base;
    public double UnalignedFraction;
    public bool DrawParametersDefault;

    public List<(string Key, byte[] Pattern, int Count)> AttributePatterns = new();
    public Dictionary<ulong, string> AttributesByVertex = new();
    public Dictionary<uint, string> AttributesByUsedMask = new();

    public Dictionary<(ulong GuestId, TextureKey Key), TexState> Textures = new();
    public Dictionary<ulong, ComputeState> ComputeByGuest = new();

    /// <summary>Texture array lengths the game used, per shader code, and every value seen game-wide.</summary>
    public Dictionary<(ulong GuestId, int Slot), int> ArrayFromBufferByGuest = new();
    public Dictionary<(ulong GuestId, bool IsSampler), int> ArrayFromPoolByGuest = new();
    public Dictionary<int, HashSet<int>> ArrayFromBufferValues = new();
    public Dictionary<bool, HashSet<int>> ArrayFromPoolValues = new();

    public Dictionary<(ulong Vertex, ulong Fragment), byte[]> PipelineByPair = new();
    public Dictionary<ulong, byte[]> PipelineByVertex = new();
    public Dictionary<ulong, byte[]> PipelineByFragment = new();
    public Dictionary<string, byte[]> PipelineBySignature = new();
    public Dictionary<uint, byte[]> PipelineByUsedMask = new();
    public byte[] PipelineDefault;

    /// <summary>Programs already in the cache, by guest code identity of each stage and the unaligned storage flag.</summary>
    public HashSet<string> Existing = new();

    public HashSet<ulong> KnownGuest = new();

    public static string Signature(ulong[] guestIds, bool unaligned) => string.Join(",", guestIds) + (unaligned ? ",u" : ",a");

    /// <summary>
    /// Learns from the first <paramref name="limit"/> programs of the cache (all of them by default). Programs RyuSAK
    /// added itself (<paramref name="built"/>) only count as present: their settings were guesses, not the game's.
    /// </summary>
    public static Profile Learn(RyubingCache cache, Func<int, uint> usedAttributesOfGuest, int limit = int.MaxValue, HashSet<ulong> built = null)
    {
        Profile p = new();
        List<CacheProgram> all = cache.Programs.Take(limit).ToList();

        foreach (CacheProgram program in all)
        {
            ulong[] ids = new ulong[6];
            for (int stage = 0; stage < 6; stage++)
            {
                if (program.GuestIndex[stage] >= 0)
                {
                    ids[stage] = cache.Guest[program.GuestIndex[stage]].Id;
                }
            }

            bool unaligned = program.IsCompute ? program.State.Comp.HasUnalignedStorageBuffer : program.State.Gfx.HasUnalignedStorageBuffer;
            p.Existing.Add(Signature(ids, unaligned));
        }

        List<CacheProgram> programs = built == null || built.Count == 0 ? all : all.Where(x => !built.Contains(x.Fingerprint)).ToList();
        p.BuiltPrograms = all.Count - programs.Count;
        List<CacheProgram> graphics = programs.Where(x => !x.IsCompute).ToList();
        p.GraphicsPrograms = graphics.Count;
        p.ComputePrograms = programs.Count - graphics.Count;

        foreach (CacheProgram program in programs)
        {
            ulong[] ids = new ulong[6];
            for (int stage = 0; stage < 6; stage++)
            {
                if (program.GuestIndex[stage] >= 0)
                {
                    GuestEntry guest = cache.Guest[program.GuestIndex[stage]];
                    ids[stage] = guest.Id;
                    p.KnownGuest.Add(guest.Id);
                }
            }

            if (program.State.Pipeline != null && PlainGraphics(program))
            {
                p.PipelineBySignature.TryAdd(string.Join(",", ids), program.State.Pipeline);
            }

            foreach ((TextureKey key, TexState state) in program.State.Textures)
            {
                int stage = program.IsCompute ? 0 : key.StageIndex + 1;
                if (stage < 6 && ids[stage] != 0)
                {
                    p.Textures[(ids[stage], key)] = state;
                }
            }

            foreach ((TextureKey key, int length) in program.State.ArrayFromBuffer)
            {
                int stage = program.IsCompute ? 0 : key.StageIndex + 1;
                if (stage < 6 && ids[stage] != 0)
                {
                    p.ArrayFromBufferByGuest[(ids[stage], key.CbufSlot)] = length;
                }

                if (!p.ArrayFromBufferValues.TryGetValue(key.CbufSlot, out HashSet<int> values))
                {
                    p.ArrayFromBufferValues[key.CbufSlot] = values = new HashSet<int>();
                }

                values.Add(length);
            }

            foreach ((bool isSampler, int length) in program.State.ArrayFromPool)
            {
                // Pool lengths come from the bound texture and sampler pools, not from a stage, so they apply to every stage of the program.
                foreach (ulong id in ids.Where(id => id != 0))
                {
                    p.ArrayFromPoolByGuest[(id, isSampler)] = length;
                }

                if (!p.ArrayFromPoolValues.TryGetValue(isSampler, out HashSet<int> values))
                {
                    p.ArrayFromPoolValues[isSampler] = values = new HashSet<int>();
                }

                values.Add(length);
            }

            if (program.IsCompute)
            {
                p.ComputeByGuest[ids[0]] = program.State.Comp;
            }
        }

        if (graphics.Count == 0)
        {
            return p;
        }

        GfxState b = default;
        b.EarlyZForce = Mode(graphics, x => x.State.Gfx.EarlyZForce);
        b.Topology = Mode(graphics, x => x.State.Gfx.Topology);
        b.TessMode = Mode(graphics, x => x.State.Gfx.TessMode);
        b.ViewportTransformDisable = Mode(graphics, x => x.State.Gfx.ViewportTransformDisable);
        b.DepthMode = Mode(graphics, x => x.State.Gfx.DepthMode);
        b.ProgramPointSizeEnable = Mode(graphics, x => x.State.Gfx.ProgramPointSizeEnable);
        b.PointSize = Mode(graphics, x => x.State.Gfx.PointSize);
        b.YNegateEnabled = Mode(graphics, x => x.State.Gfx.YNegateEnabled);
        b.AlphaToCoverageEnable = Mode(graphics, x => x.State.Gfx.AlphaToCoverageEnable);
        string outputs = Mode(graphics, x => Hex(x.State.Gfx.FragmentOutputTypes));
        Span<byte> outputSpan = b.FragmentOutputTypes;
        Convert.FromHexString(outputs).CopyTo(outputSpan);
        p.DrawParametersDefault = Mode(graphics, x => x.State.Gfx.HasConstantBufferDrawParameters);
        b.HasConstantBufferDrawParameters = p.DrawParametersDefault;
        p.Base = b;
        p.UnalignedFraction = graphics.Count(x => x.State.Gfx.HasUnalignedStorageBuffer) / (double)graphics.Count;

        foreach (var g in graphics.GroupBy(x => x.State.AttributeKey).OrderByDescending(g => g.Count()))
        {
            p.AttributePatterns.Add((g.Key, Convert.FromHexString(g.Key), g.Count()));
        }

        foreach (var g in graphics.Where(x => x.GuestIndex[1] >= 0).GroupBy(x => cache.Guest[x.GuestIndex[1]].Id))
        {
            p.AttributesByVertex[g.Key] = g.GroupBy(x => x.State.AttributeKey).OrderByDescending(a => a.Count()).First().Key;
        }

        if (usedAttributesOfGuest != null)
        {
            foreach (var g in graphics.Where(x => x.GuestIndex[1] >= 0).GroupBy(x => usedAttributesOfGuest(x.GuestIndex[1])))
            {
                p.AttributesByUsedMask[g.Key] = g.GroupBy(x => x.State.AttributeKey).OrderByDescending(a => a.Count()).First().Key;
            }
        }

        // Pipeline state from programs with tessellation or geometry stages describes a different setup (patch topology),
        // which Vulkan doesn't allow for vertex and fragment shaders alone, so only plain programs are used.
        List<CacheProgram> withPipeline = graphics.Where(x => x.State.Pipeline != null && PlainGraphics(x)).ToList();
        foreach (CacheProgram program in withPipeline)
        {
            ulong vertex = program.GuestIndex[1] >= 0 ? cache.Guest[program.GuestIndex[1]].Id : 0;
            ulong fragment = program.GuestIndex[5] >= 0 ? cache.Guest[program.GuestIndex[5]].Id : 0;
            p.PipelineByPair.TryAdd((vertex, fragment), program.State.Pipeline);
            p.PipelineByVertex.TryAdd(vertex, program.State.Pipeline);
            p.PipelineByFragment.TryAdd(fragment, program.State.Pipeline);

            if (usedAttributesOfGuest != null && program.GuestIndex[1] >= 0)
            {
                p.PipelineByUsedMask.TryAdd(usedAttributesOfGuest(program.GuestIndex[1]), program.State.Pipeline);
            }
        }

        p.PipelineDefault = withPipeline
            .GroupBy(x => Convert.ToHexString(x.State.Pipeline))
            .OrderByDescending(g => g.Count())
            .Select(g => g.First().State.Pipeline)
            .FirstOrDefault();

        return p;
    }

    private static bool PlainGraphics(CacheProgram program)
    {
        return !program.IsCompute && program.GuestIndex[0] < 0 && program.GuestIndex[2] < 0 && program.GuestIndex[3] < 0 && program.GuestIndex[4] < 0;
    }

    private static string Hex(Bytes8 value)
    {
        ReadOnlySpan<byte> span = value;
        return Convert.ToHexString(span);
    }

    private static T Mode<T>(List<CacheProgram> programs, Func<CacheProgram, T> selector)
    {
        return programs.GroupBy(selector).OrderByDescending(g => g.Count()).First().Key;
    }

    /// <summary>
    /// Vertex attribute types for a vertex shader: what the game used with this shader before, else what it used
    /// with shaders reading the same attributes, else the game's most common setup.
    /// </summary>
    public byte[] AttributesFor(ulong vertexGuestId, uint usedAttributes)
    {
        if (AttributesByVertex.TryGetValue(vertexGuestId, out string key) || AttributesByUsedMask.TryGetValue(usedAttributes, out key))
        {
            return Convert.FromHexString(key);
        }

        return AttributePatterns.Count > 0 ? AttributePatterns[0].Pattern : new byte[32];
    }

    /// <summary>
    /// Pipeline state for Ryubing to prepare the program with at boot: the state the game drew this vertex shader with,
    /// else the state of a shader reading the same vertex attributes. Null when neither is known.
    /// </summary>
    public byte[] PipelineFor(ulong vertex, ulong fragment, uint usedAttributes)
    {
        if (PipelineByPair.TryGetValue((vertex, fragment), out byte[] pipeline)
            || PipelineByVertex.TryGetValue(vertex, out pipeline)
            || PipelineByUsedMask.TryGetValue(usedAttributes, out pipeline))
        {
            return pipeline;
        }

        return null;
    }

    /// <summary>
    /// Length of a texture array sized from a constant buffer: the value used with this code before, else the only value the game ever used.
    /// </summary>
    public bool TryArrayFromBuffer(ulong guestId, int slot, out int length)
    {
        if (ArrayFromBufferByGuest.TryGetValue((guestId, slot), out length))
        {
            return true;
        }

        if (ArrayFromBufferValues.TryGetValue(slot, out HashSet<int> values) && values.Count == 1)
        {
            length = values.First();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Length of a texture array sized from the texture or sampler pool, chosen the same way.
    /// </summary>
    public bool TryArrayFromPool(ulong guestId, bool isSampler, out int length)
    {
        if (ArrayFromPoolByGuest.TryGetValue((guestId, isSampler), out length))
        {
            return true;
        }

        if (ArrayFromPoolValues.TryGetValue(isSampler, out HashSet<int> values) && values.Count == 1)
        {
            length = values.First();
            return true;
        }

        return false;
    }
}
