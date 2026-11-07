using System.Collections.Concurrent;
using Ryujinx.Graphics.Shader;

namespace RyuSAK.ShaderBuilder;

sealed class PlanOptions
{
    /// <summary>Also add a copy of each program for draws with unaligned storage buffers. Null decides from what the game did before.</summary>
    public bool? Unaligned;
    public int MaxPrograms = int.MaxValue;

    /// <summary>
    /// Pipeline state to save with new graphics entries, which makes Ryubing prepare a GPU pipeline for them at boot.
    /// "all" guesses one for every entry, "learned" only uses state the game used with the same vertex shader, "none" saves none.
    /// </summary>
    public string Pipeline = "all";

    /// <summary>How likely the game is to use a program (lower first); the build is filled in that order. Optional.</summary>
    public Func<ProgramKey, int> Rank;

    /// <summary>Whether to build a program. Optional; all programs are eligible by default.</summary>
    public Func<ProgramKey, bool> Include;
}

sealed class PlannedProgram
{
    public FoundProgram Source;
    public NewProgram Entry;
    public ulong[] GuestIds;
    public bool Unaligned;
    public bool Learned;
}

sealed class Plan
{
    public int ProgramsFound;
    public int AlreadyCached;
    public int CappedOut;
    public Dictionary<string, int> Skipped = new();
    public List<PlannedProgram> Programs = new();

    /// <summary>Why a program was left out, by the guest code of its stages (when that much was worked out).</summary>
    public Dictionary<string, string> SkippedBySignature = new();

    public void Skip(string reason) => Skipped[reason] = Skipped.GetValueOrDefault(reason) + 1;

    public void Skip(string reason, ulong[] guestIds)
    {
        Skip(reason);
        SkippedBySignature.TryAdd(string.Join(",", guestIds), reason);
    }
}

static class Planner
{
    // Ryubing's TextureTarget.Texture2D, the most common texture type by far.
    private const byte Texture2D = 1;

    public static Dictionary<ulong, StageProbe> ProbeAll(ShaderFinder finder, Profile profile, Action<int, int> progress)
    {
        ConcurrentDictionary<ulong, StageProbe> probes = new();
        List<StageBlob> blobs = finder.Blobs.Values.ToList();
        int done = 0;

        Parallel.ForEach(blobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, blob =>
        {
            ComputeState compute = blob.BnshStage == 5 ? ComputeControl.Read(blob.Control) : default;
            probes[blob.Id] = Prober.Probe(blob, profile.Base, compute);

            int n = Interlocked.Increment(ref done);
            if ((n & 511) == 0)
            {
                progress?.Invoke(n, blobs.Count);
            }
        });

        progress?.Invoke(blobs.Count, blobs.Count);
        return new Dictionary<ulong, StageProbe>(probes);
    }

    public static Plan Build(ShaderFinder finder, Dictionary<ulong, StageProbe> probes, Profile profile, PlanOptions options)
    {
        Plan plan = new() { ProgramsFound = finder.Programs.Count };
        bool unalignedVariants = options.Unaligned ?? profile.UnalignedFraction >= 0.02;
        List<PlannedProgram> candidates = new();
        HashSet<ulong> usedContainers = new();
        Dictionary<ulong, byte[]> containerPipeline = new();

        foreach (FoundProgram found in finder.Programs.Values)
        {
            ProgramKey key = found.Key;
            bool isCompute = key.Compute != 0;

            if (isCompute && (key.Vertex | key.Fragment | key.Geometry | key.TessControl | key.TessEval) != 0)
            {
                plan.Skip("mixed compute and graphics stages");
                continue;
            }

            if (!isCompute && key.Vertex == 0)
            {
                plan.Skip("no vertex shader");
                continue;
            }

            if (key.Geometry != 0 || key.TessControl != 0 || key.TessEval != 0)
            {
                plan.Skip("geometry or tessellation shader");
                continue;
            }

            int[] bnshStages = isCompute ? new[] { 5 } : new[] { 0, 4 };
            StageCode[] stages = new StageCode[isCompute ? 1 : 6];
            ulong[] guestIds = new ulong[6];
            Dictionary<int, StageProbe> stageProbes = new();
            string skip = null;

            foreach (int bnshStage in bnshStages)
            {
                ulong blobId = key[bnshStage];
                if (blobId == 0)
                {
                    continue;
                }

                StageBlob blob = finder.Blobs[blobId];
                StageProbe probe = probes[blobId];

                if (probe.Error != null)
                {
                    skip = "shader could not be translated";
                    break;
                }

                if (probe.QueriedConstantBufferUse)
                {
                    skip = "indirect constant buffer access";
                    break;
                }

                StageCode code = Prober.GuestCodeOf(blob, probe);
                int ryuStage = isCompute ? 0 : bnshStage + 1;
                stages[ryuStage] = code;
                guestIds[ryuStage] = RyubingCache.IdOf(code.Code, code.Cb1);
                stageProbes[ryuStage] = probe;
            }

            if (skip != null)
            {
                plan.Skip(skip);
                continue;
            }

            SpecState state = new() { Compute = isCompute };
            if (isCompute)
            {
                if (!profile.ComputeByGuest.TryGetValue(guestIds[0], out state.Comp))
                {
                    state.Comp = ComputeControl.Read(finder.Blobs[key.Compute].Control);
                    if (!ComputeControl.Trusted)
                    {
                        plan.Skip("compute shader settings unknown");
                        continue;
                    }
                }
            }
            else
            {
                state.Gfx = profile.Base;
                byte[] attributes = profile.AttributesFor(guestIds[1], stageProbes[1].UsedAttributes);
                Span<byte> attributeSpan = state.Gfx.AttributeTypes;
                attributes.CopyTo(attributeSpan);
                state.Pipeline = options.Pipeline == "none" ? null : profile.PipelineFor(guestIds[1], guestIds[5], stageProbes[1].UsedAttributes);

                // NVN sets early Z from the fragment shader, and Ryubing never re-checks it, so take it from the shader itself.
                state.Gfx.EarlyZForce = key.Fragment != 0 && NvnControl.EarlyFragmentTests(finder.Blobs[key.Fragment].Control);
            }

            // Every texture the translator registers or asks about needs an entry, or Ryubing can't load the program.
            foreach ((int ryuStage, StageProbe probe) in stageProbes)
            {
                int stageIndex = isCompute ? 0 : ryuStage - 1;
                foreach (((int handle, int cbufSlot), int flags) in probe.Textures)
                {
                    TextureKey textureKey = new(stageIndex, handle, cbufSlot);
                    bool learned = profile.Textures.TryGetValue((guestIds[ryuStage], textureKey), out TexState texState);

                    if (!learned && (flags & QueriedFlags.TextureFormat) != 0)
                    {
                        skip = "texture format unknown";
                        break;
                    }

                    // Ryubing never re-checks a texture's type, so only answer "2D" where every use of the texture in this shader is 2D.
                    if (!learned && (flags & QueriedFlags.SamplerType) != 0
                        && (!probe.TypesByHandle.TryGetValue((handle, cbufSlot), out HashSet<SamplerType> types) || types.Any(t => t != SamplerType.Texture2D)))
                    {
                        skip = "texture type unknown";
                        break;
                    }

                    if (!learned)
                    {
                        texState = new TexState { TextureTarget = Texture2D, CoordNormalized = true };
                    }

                    texState.QueriedFlags = flags;
                    state.Textures.Add((textureKey, texState));
                }

                if (skip != null)
                {
                    break;
                }

                foreach (int slot in probe.ArrayFromBufferSlots)
                {
                    if (!profile.TryArrayFromBuffer(guestIds[ryuStage], slot, out int length))
                    {
                        skip = "texture array size unknown";
                        break;
                    }

                    state.ArrayFromBuffer.Add((new TextureKey(stageIndex, 0, slot), length));
                    state.QueriedState |= QueriedFlags.TextureArrayFromBuffer;
                }

                foreach (bool isSampler in new[] { false, true })
                {
                    if (skip != null || !(isSampler ? probe.ArrayFromSamplerPool : probe.ArrayFromTexturePool)
                        || state.ArrayFromPool.Any(a => a.IsSampler == isSampler))
                    {
                        continue;
                    }

                    if (!profile.TryArrayFromPool(guestIds[ryuStage], isSampler, out int length))
                    {
                        skip = "texture array size unknown";
                        break;
                    }

                    state.ArrayFromPool.Add((isSampler, length));
                    state.QueriedState |= QueriedFlags.TextureArrayFromPool;
                }

                if (skip != null)
                {
                    break;
                }
            }

            if (skip != null)
            {
                plan.Skip(skip, guestIds);
                continue;
            }

            bool learnedCode = guestIds.Any(id => id != 0 && profile.KnownGuest.Contains(id));
            bool excluded = options.Include != null && !options.Include(key);
            bool exclusionReported = false;

            foreach (bool unaligned in unalignedVariants ? new[] { false, true } : new[] { false })
            {
                if (profile.Existing.Contains(Profile.Signature(guestIds, unaligned)))
                {
                    plan.AlreadyCached++;
                    if (profile.Played.Contains(Profile.Signature(guestIds, unaligned)))
                    {
                        usedContainers.UnionWith(found.ContainerIds);
                    }

                    if (profile.PipelineBySignature.TryGetValue(string.Join(",", guestIds), out byte[] usedPipeline))
                    {
                        foreach (ulong containerId in found.ContainerIds)
                        {
                            containerPipeline.TryAdd(containerId, usedPipeline);
                        }
                    }

                    continue;
                }

                if (excluded)
                {
                    if (!exclusionReported)
                    {
                        plan.Skip("unlikely to be used");
                        exclusionReported = true;
                    }

                    continue;
                }

                SpecState variant = state.Clone();
                if (isCompute)
                {
                    variant.Comp.HasUnalignedStorageBuffer = unaligned;
                }
                else
                {
                    variant.Gfx.HasUnalignedStorageBuffer = unaligned;
                }

                candidates.Add(new PlannedProgram
                {
                    Source = found,
                    Entry = new NewProgram { Stages = stages, State = variant },
                    GuestIds = guestIds,
                    Unaligned = unaligned,
                    Learned = learnedCode,
                });
            }
        }

        // Programs with no closer match get the pipeline state of a program the game used from the same shader file,
        // else the game's most common one. Ryubing then prepares them at boot with "close enough" state, which lets
        // the GPU driver reuse most of the work for the real draw.
        foreach (PlannedProgram planned in candidates)
        {
            SpecState state = planned.Entry.State;
            if (options.Pipeline == "all" && !state.Compute && state.Pipeline == null)
            {
                state.Pipeline = planned.Source.ContainerIds.Select(id => containerPipeline.GetValueOrDefault(id)).FirstOrDefault(p => p != null)
                    ?? profile.PipelineDefault;
            }
        }

        // When capped, keep the programs the game is most likely to use: by the game's model materials when known,
        // then programs whose code the game already used, programs from shader files that hold a program the game
        // used, and the ones listed most often.
        IEnumerable<PlannedProgram> ordered = candidates
            .OrderBy(p => options.Rank?.Invoke(p.Source.Key) ?? 0)
            .ThenByDescending(p => p.Learned && p.Source.SharesContainerWith(usedContainers))
            .ThenByDescending(p => p.Source.SharesContainerWith(usedContainers))
            .ThenByDescending(p => p.Learned)
            .ThenBy(p => p.Unaligned)
            .ThenByDescending(p => p.Source.Occurrences);

        foreach (PlannedProgram program in ordered)
        {
            if (plan.Programs.Count >= options.MaxPrograms)
            {
                plan.CappedOut++;
                continue;
            }

            plan.Programs.Add(program);
        }

        return plan;
    }
}

/// <summary>
/// Compute shader settings stored in an NVN control section.
/// </summary>
static class ComputeControl
{
    // Offsets of the compute block in NVNshaderControl. Ryubing needs the exact values, so compute shaders are
    // only added when these have been checked against a real cache (see the evaluate command).
    public static int BlockDimsOffset = 0x748;
    public static int SharedMemoryOffset = 0x754;
    public static int LocalMemoryOffset = 0x758;
    public static bool Trusted;

    public static ComputeState Read(byte[] control)
    {
        if (control.Length < LocalMemoryOffset + 4)
        {
            return new ComputeState { LocalSizeX = 1, LocalSizeY = 1, LocalSizeZ = 1 };
        }

        ComputeState state = new()
        {
            LocalSizeX = (int)NvnControl.ReadU32(control, BlockDimsOffset),
            LocalSizeY = (int)NvnControl.ReadU32(control, BlockDimsOffset + 4),
            LocalSizeZ = (int)NvnControl.ReadU32(control, BlockDimsOffset + 8),
            SharedMemorySize = (int)NvnControl.ReadU32(control, SharedMemoryOffset),
            LocalMemorySize = (int)NvnControl.ReadU32(control, LocalMemoryOffset),
        };

        if (state.LocalSizeX <= 0 || state.LocalSizeX > 1024 || state.LocalSizeY <= 0 || state.LocalSizeY > 1024 || state.LocalSizeZ <= 0 || state.LocalSizeZ > 64)
        {
            state.LocalSizeX = 1;
            state.LocalSizeY = 1;
            state.LocalSizeZ = 1;
        }

        return state;
    }
}
