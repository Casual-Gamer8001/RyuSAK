using System.Text;

namespace RyuSAK.ShaderBuilder;

/// <summary>
/// Measures how well generated entries would have served a game: it learns from the first part of a real cache,
/// plans entries from the game files, then checks every later program in the real cache against Ryubing's own
/// matching rules (ShaderSpecializationState.MatchesGraphics on a non-MoltenVK Vulkan GPU).
/// </summary>
static class Evaluator
{
    public static string Run(RyubingCache full, int learnCount, ShaderFinder finder, Dictionary<ulong, StageProbe> probes, Profile profile, Plan plan)
    {
        StringBuilder sb = new();
        sb.AppendLine($"Real cache: {full.Programs.Count} programs ({full.Programs.Count(p => !p.IsCompute)} graphics), {full.Guest.Count} guest codes. Learned from the first {learnCount}.");
        sb.AppendLine($"Profile: depth -1..1={profile.Base.DepthMode}, y negate={profile.Base.YNegateEnabled}, draw parameters={profile.DrawParametersDefault}, unaligned share={profile.UnalignedFraction:P1}, attribute patterns={profile.AttributePatterns.Count}");
        sb.AppendLine($"Game files: {finder.Programs.Count} distinct BNSH programs, {finder.Blobs.Count} distinct stages; probe errors={probes.Values.Count(p => p.Error != null)}");
        sb.AppendLine($"Plan: {plan.Programs.Count} new entries ({plan.Programs.Count(p => p.Unaligned)} unaligned copies), already cached={plan.AlreadyCached}, capped={plan.CappedOut}");
        sb.AppendLine("  skipped: " + string.Join(", ", plan.Skipped.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));

        ValidateCode(sb, full, finder, probes);

        Dictionary<string, List<(int Index, PlannedProgram Program)>> bySignature = new();
        for (int i = 0; i < plan.Programs.Count; i++)
        {
            PlannedProgram planned = plan.Programs[i];
            string signature = string.Join(",", planned.GuestIds);
            if (!bySignature.TryGetValue(signature, out var list))
            {
                bySignature[signature] = list = new();
            }

            list.Add((i, planned));
        }

        HashSet<ulong> foundGuest = new();
        foreach (PlannedProgram planned in plan.Programs)
        {
            foreach (ulong id in planned.GuestIds)
            {
                foundGuest.Add(id);
            }
        }

        int total = 0, hits = 0, pipelineHits = 0, pipelinePrepared = 0, alreadyLearned = 0;
        Dictionary<string, int> misses = new();
        Dictionary<string, int> stateMisses = new();
        Dictionary<string, int> unverified = new();
        List<int> firstMatchIndex = new();

        // The real cache doesn't store whether a vertex shader reads draw parameters, so ask the translator about the same code.
        Dictionary<ulong, bool> drawParametersByVertex = new();
        Dictionary<ulong, bool> storageByGuest = new();
        foreach (StageBlob blob in finder.Blobs.Values)
        {
            if (probes.TryGetValue(blob.Id, out StageProbe probe) && probe.Error == null)
            {
                StageCode code = Prober.GuestCodeOf(blob, probe);
                ulong id = RyubingCache.IdOf(code.Code, code.Cb1);
                storageByGuest[id] = probe.QueriedUnalignedStorage;
                if (blob.BnshStage == 0)
                {
                    drawParametersByVertex[id] = probe.UsesDrawParameters;
                }
            }
        }

        Dictionary<string, int> unalignedUse = new();

        foreach (CacheProgram program in full.Programs.Skip(learnCount))
        {
            if (program.IsCompute)
            {
                continue;
            }

            total++;
            ulong[] ids = new ulong[6];
            for (int stage = 0; stage < 6; stage++)
            {
                if (program.GuestIndex[stage] >= 0)
                {
                    ids[stage] = full.Guest[program.GuestIndex[stage]].Id;
                }
            }

            {
                bool known = ids.Where(id => id != 0).All(storageByGuest.ContainsKey);
                string use = !known ? "unknown code" : ids.Any(id => id != 0 && storageByGuest[id]) ? "uses storage" : "no storage";
                string key = $"{(program.State.Gfx.HasUnalignedStorageBuffer ? "unaligned" : "aligned")}/{use}";
                unalignedUse[key] = unalignedUse.GetValueOrDefault(key) + 1;
            }

            if (profile.Existing.Contains(Profile.Signature(ids, program.State.Gfx.HasUnalignedStorageBuffer)))
            {
                alreadyLearned++;
                hits++;
                firstMatchIndex.Add(-1);
                continue;
            }

            if (!bySignature.TryGetValue(string.Join(",", ids), out var candidates))
            {
                string reason = plan.SkippedBySignature.TryGetValue(string.Join(",", ids), out string skipped) ? "skipped: " + skipped
                    : program.GuestIndex[4] >= 0 || program.GuestIndex[2] >= 0 ? "geometry/tessellation"
                    : ids.Where(id => id != 0).All(foundGuest.Contains) ? "stages exist but not as one program"
                    : "code not in a BNSH program";
                misses[reason] = misses.GetValueOrDefault(reason) + 1;
                continue;
            }

            bool usesDrawParameters = drawParametersByVertex.GetValueOrDefault(ids[1], true);
            string firstReason = null;
            int matchIndex = -1;
            bool pipelineMatched = false;

            foreach ((int index, PlannedProgram candidate) in candidates)
            {
                string reason = Mismatch(candidate.Entry.State, program.State, usesDrawParameters);
                if (reason == null)
                {
                    if (matchIndex < 0)
                    {
                        matchIndex = index;
                        string risky = UnverifiedDifference(candidate.Entry.State, program.State);
                        if (risky != null)
                        {
                            unverified[risky] = unverified.GetValueOrDefault(risky) + 1;
                        }
                    }

                    pipelineMatched |= candidate.Entry.State.Pipeline != null && program.State.Pipeline != null
                        && candidate.Entry.State.Pipeline.AsSpan().SequenceEqual(program.State.Pipeline);
                }
                else
                {
                    firstReason ??= reason;
                }
            }

            if (matchIndex >= 0)
            {
                hits++;
                firstMatchIndex.Add(matchIndex);
                if (pipelineMatched)
                {
                    pipelineHits++;
                }

                if (plan.Programs[matchIndex].Entry.State.Pipeline != null)
                {
                    pipelinePrepared++;
                }
            }
            else
            {
                stateMisses[firstReason] = stateMisses.GetValueOrDefault(firstReason) + 1;
            }
        }

        sb.AppendLine($"Programs compiled after the learning point: {total}");
        sb.AppendLine($"  would load from the cache instead: {hits} ({Pct(hits, total)}), of which {alreadyLearned} were already in the learned part");
        sb.AppendLine($"  ... prepared at boot with some pipeline state (new entries): {pipelinePrepared} ({Pct(pipelinePrepared, hits - alreadyLearned)} of new hits); exactly the state the game used: {pipelineHits} ({Pct(pipelineHits, total)})");
        sb.AppendLine($"  real programs saved with pipeline state: {full.Programs.Skip(learnCount).Count(p => !p.IsCompute && p.State.Pipeline != null)} of {total}");
        sb.AppendLine("  no entry for the code: " + string.Join(", ", misses.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        sb.AppendLine("  entry but different state: " + string.Join(", ", stateMisses.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        sb.AppendLine("  MATCHED BUT AN UNCHECKED FIELD DIFFERS (would render wrong): " + (unverified.Count == 0 ? "none" : string.Join(", ", unverified.Select(kv => $"{kv.Key}={kv.Value}"))));
        sb.AppendLine("  storage alignment of real programs: " + string.Join(", ", unalignedUse.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));

        List<string> caps = new();
        foreach (int cap in new[] { 1000, 2000, 5000, 10000, 20000, 50000 })
        {
            if (cap >= plan.Programs.Count)
            {
                break;
            }

            int covered = firstMatchIndex.Count(i => i < cap);
            caps.Add($"{cap} entries -> {Pct(covered, total)}");
        }

        caps.Add($"all {plan.Programs.Count} -> {Pct(hits, total)}");
        sb.AppendLine("  coverage by entry limit: " + string.Join(", ", caps));
        return sb.ToString();
    }

    /// <summary>
    /// Fields Ryubing does not compare when it looks a program up, so a wrong value would be used as is.
    /// </summary>
    private static string UnverifiedDifference(SpecState cached, SpecState actual)
    {
        GfxState c = cached.Gfx;
        GfxState a = actual.Gfx;

        if (c.EarlyZForce != a.EarlyZForce) return "early Z";
        if (c.ProgramPointSizeEnable != a.ProgramPointSizeEnable || (!a.ProgramPointSizeEnable && c.PointSize != a.PointSize)) return "point size";

        foreach ((TextureKey key, TexState state) in cached.Textures)
        {
            foreach ((TextureKey actualKey, TexState actualState) in actual.Textures)
            {
                if (actualKey != key)
                {
                    continue;
                }

                if ((actualState.QueriedFlags & QueriedFlags.SamplerType) != 0 && actualState.TextureTarget != state.TextureTarget) return "texture type";
                if ((actualState.QueriedFlags & QueriedFlags.TextureFormat) != 0 && (actualState.Format != state.Format || actualState.FormatSrgb != state.FormatSrgb)) return "texture format";
            }
        }

        foreach ((TextureKey key, int length) in actual.ArrayFromBuffer)
        {
            if (!cached.ArrayFromBuffer.Any(x => x.Key == key && x.Length == length)) return "texture array size (buffer)";
        }

        foreach ((bool isSampler, int length) in actual.ArrayFromPool)
        {
            if (!cached.ArrayFromPool.Any(x => x.IsSampler == isSampler && x.Length == length)) return "texture array size (pool)";
        }

        return null;
    }

    private static string Pct(int a, int b) => b == 0 ? "n/a" : $"{100.0 * a / b:F1}%";

    /// <summary>
    /// Ryubing's ShaderSpecializationState.MatchesGraphics, for a GPU that doesn't need vertex shaders converted to compute.
    /// </summary>
    public static string Mismatch(SpecState cached, SpecState actual, bool usesDrawParameters)
    {
        GfxState c = cached.Gfx;
        GfxState a = actual.Gfx;

        if (a.ViewportTransformDisable != c.ViewportTransformDisable) return "viewport transform";
        if ((a.AlphaToCoverageEnable && a.AlphaToCoverageDitherEnable) != (c.AlphaToCoverageEnable && c.AlphaToCoverageDitherEnable)) return "alpha to coverage dither";
        if (a.DepthMode != c.DepthMode) return "depth mode";
        if (a.AlphaTestEnable != c.AlphaTestEnable) return "alpha test";
        if (a.AlphaTestEnable && (a.AlphaTestCompare != c.AlphaTestCompare || a.AlphaTestReference != c.AlphaTestReference)) return "alpha test";

        ReadOnlySpan<byte> aa = a.AttributeTypes;
        ReadOnlySpan<byte> ca = c.AttributeTypes;
        if (!aa.SequenceEqual(ca)) return "vertex attribute types";

        if (usesDrawParameters && a.HasConstantBufferDrawParameters != c.HasConstantBufferDrawParameters) return "draw parameters";
        if (a.HasUnalignedStorageBuffer != c.HasUnalignedStorageBuffer) return "unaligned storage buffers";
        if (a.DualSourceBlendEnable != c.DualSourceBlendEnable) return "dual source blend";
        if (a.YNegateEnabled != c.YNegateEnabled) return "y negate";

        foreach ((TextureKey key, TexState state) in cached.Textures)
        {
            if ((state.QueriedFlags & QueriedFlags.CoordNormalized) == 0)
            {
                continue;
            }

            foreach ((TextureKey actualKey, TexState actualState) in actual.Textures)
            {
                if (actualKey == key && actualState.CoordNormalized != state.CoordNormalized)
                {
                    return "texture coordinate normalization";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks that code from the game files matches what Ryubing recorded: code length, constant data and compute settings.
    /// </summary>
    private static void ValidateCode(StringBuilder sb, RyubingCache full, ShaderFinder finder, Dictionary<ulong, StageProbe> probes)
    {
        Dictionary<string, List<GuestEntry>> byHead = new();
        foreach (GuestEntry guest in full.Guest)
        {
            if (guest.Code.Length >= 0x68)
            {
                string head = Convert.ToHexString(guest.Code.AsSpan(0, 0x68));
                if (!byHead.TryGetValue(head, out var list))
                {
                    byHead[head] = list = new();
                }

                list.Add(guest);
            }
        }

        HashSet<int> computeGuest = full.Programs.Where(p => p.IsCompute).Select(p => p.GuestIndex[0]).ToHashSet();
        Dictionary<int, StageBlob> blobByGuest = new();
        int matched = 0, sizeEqual = 0, cb1Checked = 0, cb1Equal = 0, computeChecked = 0;
        Dictionary<string, int> sizeDiffs = new();
        Dictionary<int, int> dimsOffsets = new();
        Dictionary<int, int> sharedOffsets = new();
        Dictionary<int, int> localOffsets = new();
        Dictionary<string, int> computeFields = new();

        foreach (StageBlob blob in finder.Blobs.Values)
        {
            if (blob.ByteCode.Length < 0x30 + 0x68)
            {
                continue;
            }

            string head = Convert.ToHexString(blob.ByteCode.AsSpan(0x30, 0x68));
            if (!byHead.TryGetValue(head, out var guests))
            {
                continue;
            }

            foreach (GuestEntry guest in guests)
            {
                if (0x30 + guest.Code.Length > blob.ByteCode.Length || !blob.ByteCode.AsSpan(0x30, guest.Code.Length).SequenceEqual(guest.Code))
                {
                    continue;
                }

                matched++;
                blobByGuest[guest.Index] = blob;
                StageProbe probe = probes[blob.Id];
                if (probe.Size == guest.Code.Length)
                {
                    sizeEqual++;
                }
                else
                {
                    string d = probe.Error != null ? "probe error" : (probe.Size - guest.Code.Length).ToString();
                    sizeDiffs[d] = sizeDiffs.GetValueOrDefault(d) + 1;
                }

                if (guest.Cb1.Length > 0)
                {
                    cb1Checked++;
                    byte[] constants = Prober.ConstantsOf(blob, new NvnControl(blob.Control));
                    if (constants.Length >= guest.Cb1.Length && constants.AsSpan(0, guest.Cb1.Length).SequenceEqual(guest.Cb1))
                    {
                        cb1Equal++;
                    }
                }

                if (computeGuest.Contains(guest.Index))
                {
                    computeChecked++;
                    ComputeState real = full.Programs.First(p => p.IsCompute && p.GuestIndex[0] == guest.Index).State.Comp;
                    FindOffsets(blob.Control, new[] { real.LocalSizeX, real.LocalSizeY, real.LocalSizeZ }, dimsOffsets);
                    FindOffsets(blob.Control, new[] { real.SharedMemorySize }, sharedOffsets);
                    FindOffsets(blob.Control, new[] { real.LocalMemorySize }, localOffsets);

                    ComputeState ours = ComputeControl.Read(blob.Control);
                    string fields = $"{(ours.LocalSizeX == real.LocalSizeX && ours.LocalSizeY == real.LocalSizeY && ours.LocalSizeZ == real.LocalSizeZ ? "dims ok" : $"dims {ours.LocalSizeX}x{ours.LocalSizeY}x{ours.LocalSizeZ} vs {real.LocalSizeX}x{real.LocalSizeY}x{real.LocalSizeZ}")}"
                        + $", shared {(ours.SharedMemorySize == real.SharedMemorySize ? "ok" : $"{ours.SharedMemorySize} vs {real.SharedMemorySize}")}"
                        + $", local {(ours.LocalMemorySize == real.LocalMemorySize ? "ok" : $"{ours.LocalMemorySize} vs {real.LocalMemorySize}")}";
                    computeFields[fields] = computeFields.GetValueOrDefault(fields) + 1;
                }

                break;
            }
        }

        sb.AppendLine($"Code check: {matched} cached codes found in BNSH stages; translator size equal for {sizeEqual} ({Pct(sizeEqual, matched)}); differences: {string.Join(", ", sizeDiffs.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}x{kv.Value}"))}");
        sb.AppendLine($"Constant data check: {cb1Equal}/{cb1Checked} equal to the NVN constants");

        // Early Z is set by NVN from the fragment shader, so it should follow the control section's early_fragment_tests byte.
        Dictionary<string, int> earlyZ = new();
        foreach (CacheProgram program in full.Programs.Where(p => !p.IsCompute && p.GuestIndex[5] >= 0))
        {
            if (blobByGuest.TryGetValue(program.GuestIndex[5], out StageBlob fragment))
            {
                string pair = $"control[0x{NvnControl.EarlyFragmentTestsOffset:x}]={fragment.Control[NvnControl.EarlyFragmentTestsOffset]} -> earlyZ={program.State.Gfx.EarlyZForce}";
                earlyZ[pair] = earlyZ.GetValueOrDefault(pair) + 1;
            }
        }

        sb.AppendLine("Early Z check: " + string.Join(", ", earlyZ.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}")));
        if (computeChecked > 0)
        {
            sb.AppendLine($"Compute check ({computeChecked}): dims offsets {Top(dimsOffsets)}; shared offsets {Top(sharedOffsets)}; local offsets {Top(localOffsets)}");
            sb.AppendLine("  current reader: " + string.Join(" | ", computeFields.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} x{kv.Value}")));
        }
    }

    private static string Top(Dictionary<int, int> offsets) => string.Join(" ", offsets.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"0x{kv.Key:x}:{kv.Value}"));

    private static void FindOffsets(byte[] control, int[] values, Dictionary<int, int> offsets)
    {
        for (int p = 0x6F0; p + values.Length * 4 <= control.Length; p += 4)
        {
            bool all = true;
            for (int i = 0; i < values.Length; i++)
            {
                if ((int)NvnControl.ReadU32(control, p + i * 4) != values[i])
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                offsets[p] = offsets.GetValueOrDefault(p) + 1;
            }
        }
    }
}
