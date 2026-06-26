using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WindowStorageLib
{
    internal class WindowLightManager
    {
        private readonly BEWindowStorageLib _be;

        private byte[] _lastKnownLightHsv = null;
        private int _lightUpdateGeneration = 0;

        public bool BlockBroken { get; private set; } = false;
        public byte[] CachedLightHsv => _lastKnownLightHsv;

        public WindowLightManager(BEWindowStorageLib be)
        {
            _be = be;
        }

        /// <summary>
        /// Called from Initialize to reset state when the block entity is re-initialised.
        /// </summary>
        public void Reset()
        {
            BlockBroken = false;
            _lastKnownLightHsv = null;
            _lightUpdateGeneration = 0;
        }

        /// <summary>
        /// Scans the inventory for the brightest light-emitting item and caches
        /// the result. Call whenever inventory contents change.
        /// </summary>
        public void UpdateLightCache()
        {
            var inv = _be.inv;
            if (inv == null) return;

            byte bestV = 0, bestH = 0, bestS = 0;
            for (int i = 0; i < inv.Count; i++)
            {
                var itemstack = inv[i]?.Itemstack;
                if (itemstack?.Collectible?.LightHsv == null) continue;
                byte v = itemstack.Collectible.LightHsv[2];
                if (v > bestV)
                {
                    bestV = v;
                    bestH = itemstack.Collectible.LightHsv[0];
                    bestS = itemstack.Collectible.LightHsv[1];
                }
            }
            _lastKnownLightHsv = bestV > 0 ? new byte[] { bestH, bestS, bestV } : null;
        }

        /// <summary>
        /// Checks whether the brightest light-emitting item changed after an
        /// inventory slot modification, and if so removes the old light and
        /// applies the new one. Called from OnSlotModified on the server side.
        /// </summary>
        public void RefreshLightOnSlotChange()
        {
            byte[] oldLight = CachedLightHsv;
            UpdateLightCache();
            byte[] newLight = CachedLightHsv;

            bool changed = (oldLight == null) != (newLight == null) ||
                (oldLight != null && newLight != null &&
                (oldLight[0] != newLight[0] || oldLight[1] != newLight[1] || oldLight[2] != newLight[2]));

            if (changed)
            {
                if (oldLight != null)
                    _be.Api.World.BlockAccessor.RemoveBlockLight(oldLight, _be.Pos);
                ApplyLight();
            }
        }

        /// <summary>
        /// Enqueues a block exchange on the server to force the engine to pick up
        /// the new light value. Only fires when a light-emitting item is present.
        /// </summary>
        public void ApplyLight()
        {
            if (_be.Api?.Side != EnumAppSide.Server || _be.Pos == null) return;
            if (_lastKnownLightHsv == null) return;

            int generation = ++_lightUpdateGeneration;

            // Snapshot all state before the enqueue so nothing changes under us
            var inv = _be.inv;
            ItemStack[] stacks = new ItemStack[inv.Count];
            for (int i = 0; i < inv.Count; i++)
                stacks[i] = inv[i]?.Itemstack?.Clone();

            float angle = _be.MeshAngleRad;
            bool[] snapPaneStates = (bool[])_be.paneStates.Clone();
            int[] snapStaggerModes = (int[])_be.staggerModes?.Clone() ?? Array.Empty<int>();
            float[] snapRotations = (float[])_be.rotations.Clone();
            float[] snapDepths = (float[])_be.depths.Clone();

            _be.Api.Event.EnqueueMainThreadTask(() =>
            {
                if (_lightUpdateGeneration != generation || _be.Api == null || _be.Pos == null || BlockBroken) return;
                if (_be.Api.World.BlockAccessor.GetBlock(_be.Pos).Id != _be.Block.Id) return;

                _be.Api.World.BlockAccessor.ExchangeBlock(_be.Block.Id, _be.Pos);

                if (_be.Api.World.BlockAccessor.GetBlockEntity(_be.Pos) is BEWindowStorageLib newBe)
                {
                    for (int i = 0; i < Math.Min(stacks.Length, newBe.inv.Count); i++)
                        newBe.inv[i].Itemstack = stacks[i];

                    newBe.MeshAngleRad = angle;
                    newBe.paneStates = snapPaneStates;
                    newBe.staggerModes = snapStaggerModes;
                    newBe.rotations = snapRotations;
                    newBe.depths = snapDepths;
                    newBe.LightManager.UpdateLightCache();
                    newBe.MarkDirty(true);
                }
            }, "windowlight");
        }

        /// <summary>
        /// Called from OnBlockBroken — cancels pending light updates and removes
        /// the block's light contribution from the world.
        /// </summary>
        public void OnBlockBroken()
        {
            BlockBroken = true;
            ++_lightUpdateGeneration;
            if (_be.Api?.Side == EnumAppSide.Server && _lastKnownLightHsv != null)
                _be.Api.World.BlockAccessor.RemoveBlockLight(_lastKnownLightHsv, _be.Pos);
        }

        /// <summary>
        /// Called from OnBlockRemoved — same as OnBlockBroken, kept separate
        /// in case the two paths need to diverge in future.
        /// </summary>
        public void OnBlockRemoved()
        {
            BlockBroken = true;
            ++_lightUpdateGeneration;
            if (_be.Api?.Side == EnumAppSide.Server && _lastKnownLightHsv != null)
                _be.Api.World.BlockAccessor.RemoveBlockLight(_lastKnownLightHsv, _be.Pos);
        }
    }
}
