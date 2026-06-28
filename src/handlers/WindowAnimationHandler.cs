using System;
using System.Collections.Generic;
using AttributeRenderingLibrary;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace WindowStorageLib
{
    internal class WindowAnimationHandler
    {
        private readonly BEWindowStorageLib _be;
        private readonly ICoreClientAPI _capi;

        internal BlockEntityAnimationUtil animUtil;
        internal bool AnimInitialized = false;
        private bool _initSnapPending;
        internal bool InitSnapPending => _initSnapPending;

        private static readonly Dictionary<string, Shape> _shapeCache = new();

        public WindowAnimationHandler(BEWindowStorageLib be, ICoreClientAPI capi)
        {
            _be = be;
            _capi = capi;
        }

        /// <summary>
        /// Called from Initialize — sets up the animator then plays the saved
        /// pane state animation at normal speed for smooth transitions later.
        /// Must run on the main thread via EnqueueMainThreadTask for Linux compatibility.
        /// </summary>
        public void EnqueueInitialSnap()
        {
            _initSnapPending = true;
            AnimInitialized = false;

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            {
                _capi.Event.EnqueueMainThreadTask(() =>
                {
                    _initSnapPending = false;
                    if (_be.IsDisposed) return;
                    InitializeAnimationUtil();
                    StartInitialAnimations();
                    _be.MarkMeshesDirty();
                    AnimInitialized = true;
                }, "windowstoragelib-initanim");
            }
            else
            {
                _initSnapPending = false;
                if (_be.IsDisposed) return;
                InitializeAnimationUtil();
                StartInitialAnimations();
                _be.MarkMeshesDirty();
                AnimInitialized = true;
            }
        }

        /// <summary>
        /// Called from FromTreeAttributes — enqueues an animation update only
        /// when pane state or mesh angle actually changed.
        /// AnimInitialized guard ensures the load snap always wins on first load.
        /// Must run on the main thread via EnqueueMainThreadTask for Linux compatibility.
        /// </summary>
        public void EnqueueStateSync(float previousAngle, bool[] oldPaneStates)
        {
            if (_initSnapPending) return;

            Action syncAction = () =>
            {
                if (_be.IsDisposed) return;

                bool angleChanged = Math.Abs(_be.MeshAngleRad - previousAngle) > 0.001f;
                if (angleChanged) InitializeAnimationUtil();

                bool paneChanged = false;
                if (oldPaneStates != null)
                {
                    int checkLen = Math.Min(oldPaneStates.Length, _be.paneStates.Length);
                    for (int pi = 0; pi < checkLen; pi++)
                    {
                        if (oldPaneStates[pi] != _be.paneStates[pi]) { paneChanged = true; break; }
                    }
                }

                if (animUtil != null && AnimInitialized && (paneChanged || angleChanged))
                    UpdateAnimations(false);

                if (paneChanged || angleChanged)
                    _be.MarkMeshesDirty();

                if (paneChanged) _be.SoundHandler?.PlaySlideSound();
                _be.SoundHandler?.FadeSharedSoundsToCurrentTarget();
            };

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                _capi.Event.EnqueueMainThreadTask(syncAction, "windowstoragelib-fromtree");
            else
                syncAction();
        }

        /// <summary>
        /// Starts animations for the current pane state without instant snap.
        /// Used during initial load so animations play at normal speed.
        /// </summary>
        private void StartInitialAnimations()
        {
            if (animUtil == null || _be.FrameBoxGroups == null) return;

            for (int i = 0; i < _be.FrameBoxGroups.Length; i++)
            {
                var group = _be.FrameBoxGroups[i];
                if (string.IsNullOrEmpty(group.AnimOpen) || string.IsNullOrEmpty(group.AnimClose))
                    continue;

                bool isOpen = i < _be.paneStates.Length && _be.paneStates[i];
                string targetAnim = isOpen ? group.AnimOpen : group.AnimClose;
                string oppositeAnim = isOpen ? group.AnimClose : group.AnimOpen;

                animUtil.StopAnimation(oppositeAnim);
                if (!animUtil.activeAnimationsByAnimCode.ContainsKey(targetAnim))
                {
                    float configSpeed = WindowStorageLibConfig.Current.AnimationSpeedValue;
                    animUtil.StartAnimation(new AnimationMetaData()
                    {
                        Animation = targetAnim,
                        Code = targetAnim,
                        AnimationSpeed = configSpeed,
                        EaseInSpeed = 10.0f,
                        EaseOutSpeed = 5f,
                        Weight = 1.0f,
                        BlendMode = EnumAnimationBlendMode.Add
                    });
                }
            }
        }

        /// <summary>
        /// Drives per-pane animations at the configured speed.
        /// Called from OnClientTick (every 500ms) to keep animations current,
        /// and from EnqueueStateSync when state changes.
        /// </summary>
        public void UpdateAnimations(bool instant = false)
        {
            if (animUtil == null) return;

            var frameGroups = _be.FrameBoxGroups;
            if (frameGroups == null) return;

            float configSpeed = WindowStorageLibConfig.Current.AnimationSpeedValue;

            for (int i = 0; i < frameGroups.Length; i++)
            {
                if (i >= _be.paneStates.Length) continue;

                var group = frameGroups[i];
                if (string.IsNullOrEmpty(group.AnimOpen) || string.IsNullOrEmpty(group.AnimClose))
                    continue;

                string targetAnim = _be.paneStates[i] ? group.AnimOpen : group.AnimClose;
                string oppositeAnim = _be.paneStates[i] ? group.AnimClose : group.AnimOpen;

                if (animUtil.activeAnimationsByAnimCode.ContainsKey(oppositeAnim))
                    animUtil.StopAnimation(oppositeAnim);

                if (!animUtil.activeAnimationsByAnimCode.ContainsKey(targetAnim))
                {
                    animUtil.StartAnimation(new AnimationMetaData()
                    {
                        Animation = targetAnim,
                        Code = targetAnim,
                        AnimationSpeed = configSpeed,
                        EaseInSpeed = 10.0f,
                        EaseOutSpeed = 5f,
                        Weight = 1.0f,
                        BlendMode = EnumAnimationBlendMode.Add
                    });
                }
            }
        }

        /// <summary>
        /// (Re-)creates the BlockEntityAnimationUtil using the block's animation
        /// shape and the current mesh rotation angle.
        /// Must be called on the main thread.
        /// </summary>
        public void InitializeAnimationUtil()
        {
            if (_capi == null || _be.Block == null) return;

            animUtil?.Dispose();
            animUtil = null;

            try
            {
                string shapePath = _be.Block?.Attributes?["animationShapePath"].AsString();
                if (string.IsNullOrEmpty(shapePath)) return;

                if (!_shapeCache.TryGetValue(shapePath, out Shape animShape))
                {
                    animShape = _capi.Assets.TryGet(shapePath)?.ToObject<Shape>();
                    if (animShape == null)
                    {
                        _be.Api.Logger.Warning($"[WindowStorageLib] Failed to load animation shape: {shapePath}");
                        return;
                    }
                    _shapeCache[shapePath] = animShape;
                }

                ITexPositionSource texSource;
                var arlBlockBehavior = _be.Block?.GetBehavior<BlockBehaviorShapeTexturesFromAttributes>();
                var arlBeBehavior = _be.GetBehavior<BlockEntityBehaviorShapeTexturesFromAttributes>();

                if (arlBlockBehavior != null && arlBeBehavior != null)
                {
                    var stexSource = new UniversalShapeTextureSource(_capi, _capi.BlockTextureAtlas, animShape, shapePath);
                    foreach (var (code, texture) in _be.Block.Textures)
                        stexSource.textures[code] = texture;
                    ShapeOverlayHelper.BakeVariantTextures(_capi, stexSource, arlBeBehavior.Variants, arlBlockBehavior.texturesByType);
                    texSource = stexSource;
                }
                else
                {
                    texSource = _capi.Tesselator.GetTextureSource(_be.Block);
                }

                float rot = _be.MeshAngleRad * GameMath.RAD2DEG;
                animUtil = new BlockEntityAnimationUtil(_capi, _be);
                animUtil.InitializeAnimator(
                    $"windowstoragelib-{_be.WindowStyle}",
                    animShape, texSource, new Vec3f(0, rot, 0)
                );
            }
            catch (Exception e)
            {
                _be.Api.Logger.Error($"[WindowStorageLib] Error initializing animation: {e.Message}");
                animUtil = null;
            }
        }

        public static void ClearShapeCache()
        {
            _shapeCache.Clear();
        }

        /// <summary>
        /// Stops all animations and disposes the animator safely.
        /// Called from CleanupClient.
        /// </summary>
        public void Dispose()
        {
            if (animUtil == null) return;
            try
            {
                animUtil.StopAnimation("open");
                animUtil.StopAnimation("closed");
                animUtil.Dispose();
            }
            catch (Exception e)
            {
                _be.Api?.Logger.Warning($"[WindowStorageLib] Error disposing animUtil: {e.Message}");
            }
            animUtil = null;
            AnimInitialized = false;
        }
    }
}
