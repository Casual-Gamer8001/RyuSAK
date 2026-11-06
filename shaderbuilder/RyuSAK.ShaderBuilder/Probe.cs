using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;

namespace RyuSAK.ShaderBuilder;

/// <summary>
/// What Ryubing's translator needed from the GPU state while translating one shader stage.
/// </summary>
sealed class StageProbe
{
    public int Size;
    public int Cb1Size;
    public ShaderStage Stage;
    public bool UsesDrawParameters;
    public uint UsedAttributes;
    public Dictionary<(int Handle, int CbufSlot), int> Textures = new();

    /// <summary>Texture and image types the translator settled on, by handle, from the program's binding list.</summary>
    public Dictionary<(int Handle, int CbufSlot), HashSet<SamplerType>> TypesByHandle = new();
    public bool QueriedConstantBufferUse;
    public HashSet<int> ArrayFromBufferSlots = new();
    public bool ArrayFromTexturePool;
    public bool ArrayFromSamplerPool;
    public bool QueriedUnalignedStorage;
    public string Error;

    public bool QueriedArrayLength => ArrayFromBufferSlots.Count > 0 || ArrayFromTexturePool || ArrayFromSamplerPool;
}

/// <summary>
/// Runs Ryubing's shader translator over code from the game files and records every query it makes,
/// the same queries Ryubing's GpuAccessor records into a shader's specialization state.
/// </summary>
sealed class ProbeAccessor : IGpuAccessor
{
    private const int DefaultArrayLength = 32;

    private readonly byte[] _code;
    private readonly int _codeOffset;
    private readonly byte[] _cb1;
    private readonly GfxState _gfx;
    private readonly ComputeState _compute;
    private int _textureBindings;
    private int _imageBindings;

    public StageProbe Result { get; } = new();

    public ProbeAccessor(byte[] code, int codeOffset, byte[] cb1, GfxState gfx, ComputeState compute)
    {
        _code = code;
        _codeOffset = codeOffset;
        _cb1 = cb1 ?? Array.Empty<byte>();
        _gfx = gfx;
        _compute = compute;
    }

    public ReadOnlySpan<ulong> GetCode(ulong address, int minimumSize)
    {
        int start = _codeOffset + (int)address;
        if (start >= _code.Length)
        {
            return ReadOnlySpan<ulong>.Empty;
        }

        int length = (_code.Length - start) & ~7;
        return MemoryMarshal.Cast<byte, ulong>(_code.AsSpan(start, length));
    }

    public uint ConstantBuffer1Read(int offset)
    {
        return offset >= 0 && offset + 4 <= _cb1.Length ? BinaryPrimitives.ReadUInt32LittleEndian(_cb1.AsSpan(offset)) : 0;
    }

    public SetBindingPair CreateConstantBufferBinding(int index) => new(0, index + 1);

    public SetBindingPair CreateStorageBufferBinding(int index) => new(1, index);

    public SetBindingPair CreateTextureBinding(int count, bool isBuffer)
    {
        int binding = _textureBindings;
        _textureBindings += count;
        return new SetBindingPair(2, binding);
    }

    public SetBindingPair CreateImageBinding(int count, bool isBuffer)
    {
        int binding = _imageBindings;
        _imageBindings += count;
        return new SetBindingPair(3, binding);
    }

    public int QueryComputeLocalSizeX() => _compute.LocalSizeX;

    public int QueryComputeLocalSizeY() => _compute.LocalSizeY;

    public int QueryComputeLocalSizeZ() => _compute.LocalSizeZ;

    public int QueryComputeLocalMemorySize() => _compute.LocalMemorySize;

    public int QueryComputeSharedMemorySize() => _compute.SharedMemorySize;

    public uint QueryConstantBufferUse()
    {
        Result.QueriedConstantBufferUse = true;
        return 0;
    }

    public GpuGraphicsState QueryGraphicsState() => GraphicsStateConverter.ToShaderState(_gfx, hasGeometryShader: false);

    public bool QueryHasConstantBufferDrawParameters() => _gfx.HasConstantBufferDrawParameters;

    public bool QueryHasUnalignedStorageBuffer()
    {
        Result.QueriedUnalignedStorage = true;
        return _gfx.HasUnalignedStorageBuffer || _compute.HasUnalignedStorageBuffer;
    }

    public int QuerySamplerArrayLengthFromPool()
    {
        Result.ArrayFromSamplerPool = true;
        return DefaultArrayLength;
    }

    public int QueryTextureArrayLengthFromBuffer(int slot)
    {
        Result.ArrayFromBufferSlots.Add(slot);
        return DefaultArrayLength;
    }

    public int QueryTextureArrayLengthFromPool()
    {
        Result.ArrayFromTexturePool = true;
        return DefaultArrayLength;
    }

    private void Flag(int handle, int cbufSlot, int flag)
    {
        Result.Textures.TryGetValue((handle, cbufSlot), out int flags);
        Result.Textures[(handle, cbufSlot)] = flags | flag;
    }

    public SamplerType QuerySamplerType(int handle, int cbufSlot = -1)
    {
        Flag(handle, cbufSlot, QueriedFlags.SamplerType);
        return SamplerType.Texture2D;
    }

    public TextureFormat QueryTextureFormat(int handle, int cbufSlot = -1)
    {
        Flag(handle, cbufSlot, QueriedFlags.TextureFormat);
        return TextureFormat.R8G8B8A8Unorm;
    }

    public bool QueryTextureCoordNormalized(int handle, int cbufSlot = -1)
    {
        Flag(handle, cbufSlot, QueriedFlags.CoordNormalized);
        return true;
    }

    public void RegisterTexture(int handle, int cbufSlot) => Flag(handle, cbufSlot, 0);

    public void Log(string message)
    {
        // Translator warnings are not useful here.
    }
}

static class GraphicsStateConverter
{
    /// <summary>
    /// Same conversion as GpuChannelGraphicsState.CreateShaderGraphicsState for the Vulkan backend.
    /// </summary>
    public static GpuGraphicsState ToShaderState(GfxState s, bool hasGeometryShader)
    {
        AlphaTestOp alphaTestOp = !s.AlphaTestEnable ? AlphaTestOp.Always : s.AlphaTestCompare switch
        {
            1 or 0x200 => AlphaTestOp.Never,
            2 or 0x201 => AlphaTestOp.Less,
            3 or 0x202 => AlphaTestOp.Equal,
            4 or 0x203 => AlphaTestOp.LessOrEqual,
            5 or 0x204 => AlphaTestOp.Greater,
            6 or 0x205 => AlphaTestOp.NotEqual,
            7 or 0x206 => AlphaTestOp.GreaterOrEqual,
            _ => AlphaTestOp.Always,
        };

        // PrimitiveTopology: Quads = 7, QuadStrip = 8.
        bool isQuad = s.Topology is 7 or 8;
        bool halvePrimitiveId = !hasGeometryShader && isQuad;

        Array32<AttributeType> attributes = default;
        Span<AttributeType> attributeSpan = attributes.AsSpan();
        ReadOnlySpan<byte> sourceAttributes = s.AttributeTypes;
        for (int i = 0; i < 32; i++)
        {
            attributeSpan[i] = (AttributeType)sourceAttributes[i];
        }

        Array8<AttributeType> outputs = default;
        Span<AttributeType> outputSpan = outputs.AsSpan();
        ReadOnlySpan<byte> sourceOutputs = s.FragmentOutputTypes;
        for (int i = 0; i < 8; i++)
        {
            outputSpan[i] = (AttributeType)sourceOutputs[i];
        }

        return new GpuGraphicsState(
            s.EarlyZForce,
            ToInputTopology(s.Topology, s.TessMode),
            (s.TessMode & (1 << 8)) != 0,
            (TessPatchType)(s.TessMode & 3),
            (TessSpacing)((s.TessMode >> 4) & 3),
            s.AlphaToCoverageEnable,
            s.AlphaToCoverageDitherEnable,
            s.ViewportTransformDisable,
            s.DepthMode,
            s.ProgramPointSizeEnable,
            s.PointSize,
            alphaTestOp,
            s.AlphaTestReference,
            in attributes,
            s.HasConstantBufferDrawParameters,
            in outputs,
            s.DualSourceBlendEnable,
            s.YNegateEnabled,
            originUpperLeft: true,
            halvePrimitiveId);
    }

    private static InputTopology ToInputTopology(int topology, uint tessMode) => topology switch
    {
        0 => InputTopology.Points,
        1 or 2 or 3 => InputTopology.Lines,
        10 or 11 => InputTopology.LinesAdjacency,
        4 or 5 or 6 => InputTopology.Triangles,
        12 or 13 => InputTopology.TrianglesAdjacency,
        14 => (tessMode & 3) == (uint)TessPatchType.Isolines ? InputTopology.Lines : InputTopology.Triangles,
        _ => InputTopology.Points,
    };
}

static class Prober
{
    /// <summary>
    /// Translates one stage the way Ryubing does when it loads a shader cache entry.
    /// </summary>
    public static StageProbe Probe(StageBlob blob, GfxState gfx, ComputeState compute)
    {
        bool isCompute = blob.BnshStage == 5;
        NvnControl control = new(blob.Control);
        byte[] cb1 = ConstantsOf(blob, control);
        ProbeAccessor accessor = new(blob.ByteCode, 0x30, cb1, gfx, compute);
        StageProbe result = accessor.Result;

        try
        {
            TranslationFlags flags = TranslationFlags.DebugMode | (isCompute ? TranslationFlags.Compute : 0);
            TranslatorContext context = Translator.CreateContext(0, accessor, new TranslationOptions(TargetLanguage.Spirv, TargetApi.Vulkan, flags));
            result.Size = context.Size;
            result.Cb1Size = context.Cb1DataSize;
            result.Stage = context.Stage;

            ShaderProgram program = context.Translate();
            result.UsesDrawParameters = program.Info.UsesDrawParameters;

            foreach (TextureDescriptor descriptor in program.Info.Textures.Concat(program.Info.Images))
            {
                (int, int) handle = (descriptor.HandleIndex, descriptor.CbufSlot);
                if (!result.TypesByHandle.TryGetValue(handle, out HashSet<SamplerType> types))
                {
                    result.TypesByHandle[handle] = types = new HashSet<SamplerType>();
                }

                types.Add(descriptor.Type);
            }

            if (blob.BnshStage == 0)
            {
                result.UsedAttributes = UsedVertexAttributes(blob.ByteCode);
            }

            if (result.Size <= 0 || 0x30 + result.Size > blob.ByteCode.Length)
            {
                result.Error = "code size out of range";
            }
            else if (result.Cb1Size > cb1.Length)
            {
                result.Error = "constant data out of range";
            }
        }
        catch (Exception e)
        {
            result.Error = e.GetType().Name + ": " + e.Message;
        }

        return result;
    }

    /// <summary>
    /// The shader's embedded constants, which NVN binds as constant buffer 1.
    /// </summary>
    public static byte[] ConstantsOf(StageBlob blob, NvnControl control)
    {
        long start = control.ConstBufOffset;
        long end = start + control.ConstBufSize;
        if (control.ConstBufSize == 0 || start < 0 || end > blob.ByteCode.Length)
        {
            return Array.Empty<byte>();
        }

        return blob.ByteCode.AsSpan((int)start, (int)control.ConstBufSize).ToArray();
    }

    /// <summary>
    /// Generic vertex attributes read by a vertex shader, from its shader program header (one bit per attribute).
    /// </summary>
    public static uint UsedVertexAttributes(byte[] byteCode) => UsedAttributesFromHeader(byteCode.AsSpan(0x30));

    /// <summary>
    /// Same as <see cref="UsedVertexAttributes"/>, for code that starts with the shader program header (Ryubing guest code).
    /// </summary>
    public static uint UsedAttributesFromHeader(ReadOnlySpan<byte> header)
    {
        uint used = 0;
        for (int i = 0; i < 32; i++)
        {
            int b = header[0x18 + i / 2];
            int nibble = (i & 1) == 0 ? b & 0xF : b >> 4;
            if (nibble != 0)
            {
                used |= 1u << i;
            }
        }

        return used;
    }

    /// <summary>
    /// The guest code Ryubing would record for this stage: the shader program header and the reachable code.
    /// </summary>
    public static StageCode GuestCodeOf(StageBlob blob, StageProbe probe)
    {
        byte[] code = blob.ByteCode.AsSpan(0x30, probe.Size).ToArray();
        byte[] cb1 = probe.Cb1Size > 0 ? ConstantsOf(blob, new NvnControl(blob.Control)).AsSpan(0, probe.Cb1Size).ToArray() : Array.Empty<byte>();
        return new StageCode { Code = code, Cb1 = cb1 };
    }
}
