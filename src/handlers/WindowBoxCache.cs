using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace WindowStorageLib
{
    internal class WindowBoxCache
    {
        private readonly BEWindowStorageLib _be;

        private CollisionBoxGroup[] _cachedCollisionGroups;
        private Cuboidf[][] _cachedGroupFrameBoxesOpen;
        private Cuboidf[][] _cachedGroupFrameBoxesClosed;
        private Cuboidf[] _cachedSelectionBoxes;
        private Cuboidf[] _cachedCollisionBoxes;
        private int _cachedSlotStartIndex;
        private int[] _cachedFrameStartIndex;
        private int[] _cachedFrameCounts;

        public WindowBoxCache(BEWindowStorageLib be)
        {
            _be = be;
        }

        /// <summary>
        /// Called from Initialize — parses all box-related JSON once so nothing
        /// needs to touch block attributes at runtime.
        /// </summary>
        public void ParseFromBlock(int paneCount, List<SlotGroupDef> slotGroups)
        {
            _cachedCollisionGroups = _be.Block?.Attributes?["collisionBoxGroups"]?.AsObject<CollisionBoxGroup[]>();

            _cachedGroupFrameBoxesOpen = new Cuboidf[paneCount][];
            _cachedGroupFrameBoxesClosed = new Cuboidf[paneCount][];

            JsonObject groupsAttr = _be.Block?.Attributes?["slotGroups"];
            if (groupsAttr != null && groupsAttr.Exists)
            {
                var frameArr = groupsAttr.AsArray();
                if (frameArr != null)
                {
                    for (int fi = 0; fi < paneCount && fi < frameArr.Length; fi++)
                    {
                        var fg = frameArr[fi];

                        var openBoxes = fg["openFrameBoxes"];
                        _cachedGroupFrameBoxesOpen[fi] = (openBoxes != null && openBoxes.Exists)
                            ? openBoxes.AsObject<Cuboidf[]>() : null;

                        var closedBoxes = fg["closedFrameBoxes"];
                        var staticBoxes = fg["staticFrameBoxes"];
                        _cachedGroupFrameBoxesClosed[fi] = (closedBoxes != null && closedBoxes.Exists)
                            ? closedBoxes.AsObject<Cuboidf[]>()
                            : (staticBoxes != null && staticBoxes.Exists ? staticBoxes.AsObject<Cuboidf[]>() : null);
                    }
                }
            }

            // Cache the slot start index — total active (non-stacked) slots
            _cachedSlotStartIndex = 0;
            for (int g = 0; g < slotGroups.Count; g++)
            {
                if (!_be.IsGroupStacked(g))
                    _cachedSlotStartIndex += slotGroups[g].Count;
            }

            // Cache frame start indices and counts per pane
            _cachedFrameCounts = new int[paneCount];
            _cachedFrameStartIndex = new int[paneCount];
            int running = _cachedSlotStartIndex;
            for (int i = 0; i < paneCount; i++)
            {
                _cachedFrameCounts[i] = 1;
                _cachedFrameStartIndex[i] = running;
                running += _cachedFrameCounts[i];
            }

            Invalidate();
        }

        /// <summary>
        /// Nulls the derived selection and collision box arrays so they are
        /// rebuilt on the next query. Call whenever pane state or stacking changes.
        /// </summary>
        public void Invalidate()
        {
            _cachedSelectionBoxes = null;
            _cachedCollisionBoxes = null;
        }

        /// <summary>
        /// Returns the selection-box start index for the given frame group.
        /// </summary>
        public int GetGroupFrameStartIndex(int groupIndex)
        {
            if (_cachedFrameStartIndex == null || groupIndex < 0 || groupIndex >= _cachedFrameStartIndex.Length)
                return _cachedSlotStartIndex;
            return _cachedFrameStartIndex[groupIndex];
        }

        /// <summary>
        /// Returns the open or closed frame boxes for the given group index,
        /// using the pre-parsed cache. Never touches JSON at runtime.
        /// </summary>
        public Cuboidf[] GetGroupFrameBoxes(int groupIndex)
        {
            if (_cachedGroupFrameBoxesOpen == null || _cachedGroupFrameBoxesClosed == null
                || groupIndex < 0 || groupIndex >= _cachedGroupFrameBoxesOpen.Length)
                return Array.Empty<Cuboidf>();

            bool isOpen = groupIndex < _be.paneStates.Length && _be.paneStates[groupIndex];
            var boxes = isOpen ? _cachedGroupFrameBoxesOpen[groupIndex] : _cachedGroupFrameBoxesClosed[groupIndex];
            return boxes ?? Array.Empty<Cuboidf>();
        }

        /// <summary>
        /// Builds and caches the selection box array. Returns cached result on
        /// subsequent calls until Invalidate() is called.
        /// </summary>
        public Cuboidf[] GetSelectionBoxes(List<SlotGroupDef> groups)
        {
            if (_cachedSelectionBoxes != null) return _cachedSelectionBoxes;

            Cuboidf[] allSlotBoxes = _be.Block?.SelectionBoxes ?? Array.Empty<Cuboidf>();
            var resultList = new List<Cuboidf>();

            // 1. Process item slot boxes
            int slotOffset = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                int count = groups[g].Count;
                for (int i = slotOffset; i < slotOffset + count && i < allSlotBoxes.Length; i++)
                {
                    if (!_be.IsGroupStacked(g))
                        resultList.Add(allSlotBoxes[i]);
                    else
                        resultList.Add(new Cuboidf(0, 0, 0, 0, 0, 0)); // preserve index alignment
                }
                slotOffset += count;
            }

            // 2. Process window pane frame boxes
            var frameGroups = _be.FrameBoxGroups;
            if (frameGroups != null)
            {
                for (int i = 0; i < frameGroups.Length; i++)
                {
                    if (frameGroups[i].StaticFrameBoxes != null)
                        resultList.AddRange(frameGroups[i].StaticFrameBoxes);

                    bool isOpen = i < _be.paneStates.Length && _be.paneStates[i];
                    var box = isOpen ? frameGroups[i].OpenFrameBox : frameGroups[i].ClosedFrameBox;
                    if (box != null) resultList.Add(box);
                }
            }

            _cachedSelectionBoxes = resultList.ToArray();
            return _cachedSelectionBoxes;
        }

        /// <summary>
        /// Builds and caches the collision box array. Returns cached result on
        /// subsequent calls until Invalidate() is called.
        /// </summary>
        public Cuboidf[] GetCollisionBoxes()
        {
            if (_cachedCollisionBoxes != null) return _cachedCollisionBoxes;

            var result = new List<Cuboidf>();

            // 1. Per-pane collision groups (preferred path)
            if (_cachedCollisionGroups != null)
            {
                for (int i = 0; i < _cachedCollisionGroups.Length; i++)
                {
                    var group = _cachedCollisionGroups[i];

                    if (group.StaticCollisionBoxes != null)
                        result.AddRange(group.StaticCollisionBoxes);

                    bool isOpen = i < _be.paneStates.Length && _be.paneStates[i];
                    var box = isOpen ? group.OpenCollisionBox : group.ClosedCollisionBox;
                    if (box != null) result.Add(box);
                }

                _cachedCollisionBoxes = result.ToArray();
                return _cachedCollisionBoxes;
            }

            // 2. Fallback — flat slot collision boxes from block JSON
            var slotBoxes = _be.Block?.CollisionBoxes;
            if (slotBoxes != null) result.AddRange(slotBoxes);

            // 3. Fallback — flat open/closed collision arrays
            if (_be.IsAnyWindowPaneOpen)
            {
                var openBoxes = _be.Block?.Attributes?["openCollisionBoxes"]?.AsObject<Cuboidf[]>();
                if (openBoxes != null) result.AddRange(openBoxes);
            }
            else
            {
                var closedBoxes = _be.Block?.Attributes?["closedCollisionBoxes"]?.AsObject<Cuboidf[]>();
                if (closedBoxes != null) result.AddRange(closedBoxes);
            }

            _cachedCollisionBoxes = result.ToArray();
            return _cachedCollisionBoxes;
        }
    }
}
