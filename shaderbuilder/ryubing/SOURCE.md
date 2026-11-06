# Ryubing shader translator (copied code)

The shader builder runs Ryubing's own shader translator on each shader it finds, so the cache entries it writes match what Ryubing would record itself. This folder holds a copy of that code.

* Source: https://git.ryujinx.app/projects/Ryubing
* Commit: `a61a42681119bf15fe8f3297677ae48bda83e70f` (2 November 2026, "fix updater (#197)")
* Licence: MIT, see `LICENSE.txt` (Spv.Generator also keeps its own `LICENSE`)

## What was copied

| Folder | Contents |
|---|---|
| `Ryujinx.Graphics.Shader` | Unchanged copy of `src/Ryujinx.Graphics.Shader` |
| `Spv.Generator` | Unchanged copy of `src/Spv.Generator` |
| `Ryujinx.Common` | Only the files the translator needs: `Memory/IArray.cs`, `Memory/StructArrayHelpers.cs`, `Pools/ObjectPool.cs`, `Utilities/BitUtils.cs`, `Utilities/BitfieldExtensions.cs` and `Hash128.cs`, unchanged. `Utilities/EmbeddedResources.cs` is a short replacement written for RyuSAK, because the original pulls in the rest of Ryujinx.Common. `Ryujinx.Common.csproj` is also written for RyuSAK and has no package references. |

`Directory.Build.props` uses the same settings as Ryubing's (`net10.0`, `LangVersion preview`).

## Updating

1. Download the Ryubing source at the new commit.
2. Replace `Ryujinx.Graphics.Shader` and `Spv.Generator` with the new copies, and refresh the six unchanged `Ryujinx.Common` files.
3. Update the commit above.
4. If Ryubing changed its disk cache format (`FileFormatVersionMinor` in `DiskCacheHostStorage.cs`, `VersionMinor` in `DiskCacheGuestStorage.cs`, or the fields of `GpuChannelGraphicsState`, `ShaderSpecializationState` or `ProgramPipelineState`), update `RyuSAK.ShaderBuilder/RyubingCache.cs` to match.
