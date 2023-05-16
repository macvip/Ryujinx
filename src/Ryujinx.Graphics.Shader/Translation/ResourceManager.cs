using Ryujinx.Graphics.Shader.StructuredIr;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ryujinx.Graphics.Shader.Translation
{
    class ResourceManager
    {
        private static readonly string[] _stagePrefixes = new string[] { "cp", "vp", "tcp", "tep", "gp", "fp" };

        private readonly IGpuAccessor _gpuAccessor;
        private readonly ShaderProperties _properties;
        private readonly string _stagePrefix;

        private readonly int[] _cbSlotToBindingMap;
        private readonly int[] _sbSlotToBindingMap;
        private uint _sbSlotWritten;

        private readonly Dictionary<int, int> _sbSlots;
        private readonly Dictionary<int, int> _sbSlotsReverse;

        private readonly HashSet<int> _usedConstantBufferBindings;

        public ShaderProperties Properties => _properties;

        public ResourceManager(ShaderStage stage, IGpuAccessor gpuAccessor, ShaderProperties properties)
        {
            _gpuAccessor = gpuAccessor;
            _properties = properties;
            _stagePrefix = GetShaderStagePrefix(stage);

            _cbSlotToBindingMap = new int[18];
            _sbSlotToBindingMap = new int[16];
            _cbSlotToBindingMap.AsSpan().Fill(-1);
            _sbSlotToBindingMap.AsSpan().Fill(-1);

            _sbSlots = new Dictionary<int, int>();
            _sbSlotsReverse = new Dictionary<int, int>();

            _usedConstantBufferBindings = new HashSet<int>();

            properties.AddConstantBuffer(0, new BufferDefinition(BufferLayout.Std140, 0, 0, "support_buffer", SupportBuffer.GetStructureType()));
        }

        public int GetConstantBufferBinding(int slot)
        {
            int binding = _cbSlotToBindingMap[slot];
            if (binding < 0)
            {
                binding = _gpuAccessor.QueryBindingConstantBuffer(slot);
                _cbSlotToBindingMap[slot] = binding;
                string slotNumber = slot.ToString(CultureInfo.InvariantCulture);
                AddNewConstantBuffer(binding, $"{_stagePrefix}_c{slotNumber}");
            }

            return binding;
        }

        public int GetStorageBufferBinding(int sbCbSlot, int sbCbOffset, bool write)
        {
            int slot = GetSbSlot((byte)sbCbSlot, (ushort)sbCbOffset);
            int binding = _sbSlotToBindingMap[slot];
            if (binding < 0)
            {
                binding = _gpuAccessor.QueryBindingConstantBuffer(slot);
                _sbSlotToBindingMap[slot] = binding;
                string slotNumber = slot.ToString(CultureInfo.InvariantCulture);
                AddNewStorageBuffer(binding, $"{_stagePrefix}_s{slotNumber}");
            }

            if (write)
            {
                _sbSlotWritten |= 1u << slot;
            }

            return binding;
        }

        public bool TryGetConstantBufferSlot(int binding, out int slot)
        {
            for (slot = 0; slot < _cbSlotToBindingMap.Length; slot++)
            {
                if (_cbSlotToBindingMap[slot] == binding)
                {
                    return true;
                }
            }

            slot = 0;
            return false;
        }

        public void SetUsedConstantBufferBinding(int binding)
        {
            _usedConstantBufferBindings.Add(binding);
        }

        public int GetSbSlot(byte sbCbSlot, ushort sbCbOffset)
        {
            int key = PackSbCbInfo(sbCbSlot, sbCbOffset);

            if (!_sbSlots.TryGetValue(key, out int slot))
            {
                slot = _sbSlots.Count;
                _sbSlots.Add(key, slot);
                _sbSlotsReverse.Add(slot, key);
            }

            return slot;
        }

        public (int, int) GetSbCbInfo(int slot)
        {
            if (_sbSlotsReverse.TryGetValue(slot, out int key))
            {
                return UnpackSbCbInfo(key);
            }

            throw new ArgumentException($"Invalid slot {slot}.", nameof(slot));
        }

        private static int PackSbCbInfo(int sbCbSlot, int sbCbOffset)
        {
            return sbCbOffset | ((int)sbCbSlot << 16);
        }

        private static (int, int) UnpackSbCbInfo(int key)
        {
            return ((byte)(key >> 16), (ushort)key);
        }

        public BufferDescriptor[] GetConstantBufferDescriptors()
        {
            var descriptors = new BufferDescriptor[_usedConstantBufferBindings.Count];

            int descriptorIndex = 0;

            for (int slot = 0; slot < _cbSlotToBindingMap.Length; slot++)
            {
                int binding = _cbSlotToBindingMap[slot];

                if (binding >= 0 && _usedConstantBufferBindings.Contains(binding))
                {
                    descriptors[descriptorIndex++] = new BufferDescriptor(binding, slot);
                }
            }

            if (descriptors.Length != descriptorIndex)
            {
                Array.Resize(ref descriptors, descriptorIndex);
            }

            return descriptors;
        }

        public BufferDescriptor[] GetStorageBufferDescriptors()
        {
            var descriptors = new BufferDescriptor[_sbSlots.Count];

            int descriptorIndex = 0;

            foreach ((int key, int slot) in _sbSlots)
            {
                int binding = _sbSlotToBindingMap[slot];

                if (binding >= 0)
                {
                    (int sbCbSlot, int sbCbOffset) = UnpackSbCbInfo(key);
                    descriptors[descriptorIndex++] = new BufferDescriptor(binding, slot, sbCbSlot, sbCbOffset)
                    {
                        Flags = (_sbSlotWritten & (1u << slot)) != 0 ? BufferUsageFlags.Write : BufferUsageFlags.None
                    };
                }
            }

            if (descriptors.Length != descriptorIndex)
            {
                Array.Resize(ref descriptors, descriptorIndex);
            }

            return descriptors;
        }

        private void AddNewConstantBuffer(int binding, string name)
        {
            StructureType type = new StructureType(new[]
            {
                new StructureField(AggregateType.Array | AggregateType.Vector4 | AggregateType.FP32, "data", Constants.ConstantBufferSize / 16)
            });

            _properties.AddConstantBuffer(binding, new BufferDefinition(BufferLayout.Std140, 0, binding, name, type));
        }

        private void AddNewStorageBuffer(int binding, string name)
        {
            StructureType type = new StructureType(new[]
            {
                new StructureField(AggregateType.Array | AggregateType.U32, "data", 0)
            });

            _properties.AddStorageBuffer(binding, new BufferDefinition(BufferLayout.Std430, 1, binding, name, type));
        }

        public static string GetShaderStagePrefix(ShaderStage stage)
        {
            uint index = (uint)stage;

            if (index >= _stagePrefixes.Length)
            {
                return "invalid";
            }

            return _stagePrefixes[index];
        }
    }
}