using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WindowStorageLib
{
    internal class WindowSoundHandler
    {
        private readonly BEWindowStorageLib _be;
        private readonly ICoreClientAPI _capi;

        private static readonly Dictionary<string, (BlockPos owner, ILoadedSound sound)> SharedSounds = new();

        private const float FadeSpeed = 1.5f;
        private const float SmoothSpeed = 0.08f;

        public WindowSoundHandler(BEWindowStorageLib be, ICoreClientAPI capi)
        {
            _be = be;
            _capi = capi;
        }

        /// <summary>
        /// Plays the open/close swoosh sound at the window's position.
        /// </summary>
        public void PlaySlideSound()
        {
            if (_capi == null) return;
            _capi.World.PlaySoundAt(
                new AssetLocation("game", "sounds/effect/swoosh"),
                _be.Pos.X + 0.5f, _be.Pos.Y + 0.5f, _be.Pos.Z + 0.5f,
                null, randomizePitch: true, range: 16f, volume: 0.8f
            );
        }

        /// <summary>
        /// Called from OnClientTick — manages the shared rain sound.
        /// </summary>
        public void Tick()
        {
            TickSharedRainSound();
        }

        /// <summary>
        /// Fades the shared rain sound toward its current target volume.
        /// Called from the FromTreeAttributes enqueue so volume stays correct
        /// after a network sync.
        /// </summary>
        public void FadeSharedSoundsToCurrentTarget()
        {
            if (SharedSounds.TryGetValue("rain", out var rainEntry)
                && rainEntry.sound != null && !rainEntry.sound.IsDisposed && rainEntry.sound.IsPlaying)
            {
                float target = ComputeTargetRainVolume(rainEntry.owner);
                rainEntry.sound.FadeTo(target, FadeSpeed, (_) => { });
            }
        }

        /// <summary>
        /// Stops and disposes any rain sound owned by this window.
        /// Called from CleanupClient.
        /// </summary>
        public void Dispose()
        {
            if (SharedSounds.TryGetValue("rain", out var rainEntry) && rainEntry.owner == _be.Pos)
            {
                TryDisposeSound(rainEntry.sound);
                SharedSounds.Remove("rain");
            }
        }

        // ── Private ──────────────────────────────────────────────────────────

        private void TickSharedRainSound()
        {
            bool shouldPlay = WindowStorageLibConfig.Current.EnableRainSound
                && !(_be.Block?.Attributes?["disableRainSound"].AsBool(false) ?? false)
                && IsRainedOn(_be.Pos)
                && PlayerNearThisWindow((float)WindowStorageLibConfig.Current.RainSoundRange);

            if (SharedSounds.TryGetValue("rain", out var existing))
            {
                bool soundAlive = existing.sound != null && !existing.sound.IsDisposed && existing.sound.IsPlaying;

                if (soundAlive)
                {
                    existing.sound.SetPosition(_capi.World.Player.Entity.Pos.XYZFloat);

                    float target = ComputeTargetRainVolume(existing.owner);
                    existing.sound.SetVolume(GameMath.Lerp(existing.sound.Params.Volume, target, SmoothSpeed));

                    if (!shouldPlay && existing.owner == _be.Pos)
                    {
                        existing.sound.FadeOutAndStop(FadeSpeed);
                        SharedSounds.Remove("rain");
                    }
                    return;
                }

                TryDisposeSound(existing.sound);
                SharedSounds.Remove("rain");
            }

            if (!shouldPlay) return;

            ILoadedSound newSound = _capi.World.LoadSound(new SoundParams()
            {
                Location = new AssetLocation("game", "sounds/environment/rainwindow"),
                ShouldLoop = true,
                Position = _capi.World.Player.Entity.Pos.XYZFloat,
                DisposeOnFinish = false,
                Volume = ComputeTargetRainVolume(_be.Pos),
                Pitch = 1.0f
            });

            if (newSound != null)
            {
                newSound.Start();
                SharedSounds["rain"] = (_be.Pos, newSound);
            }
        }

        private bool PlayerNearThisWindow(float range)
        {
            if (_capi == null) return false;
            Vec3d playerPos = _capi.World.Player.Entity.Pos.XYZ;
            double dx = playerPos.X - (_be.Pos.X + 0.5);
            double dy = playerPos.Y - (_be.Pos.Y + 0.5);
            double dz = playerPos.Z - (_be.Pos.Z + 0.5);
            return (dx * dx + dy * dy + dz * dz) <= (range * range);
        }

        private bool IsRainedOn(BlockPos pos)
        {
            if (_capi == null) return false;
            ClimateCondition cond = _capi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
            if (cond == null || cond.Rainfall <= 0.1f || cond.Temperature <= 3f) return false;
            return _capi.World.BlockAccessor.GetRainMapHeightAt(pos) <= pos.Y ||
                   _capi.World.BlockAccessor.GetDistanceToRainFall(pos, 3) <= 2;
        }

        private float ComputeTargetRainVolume(BlockPos ownerPos)
        {
            ClimateCondition cond = _capi.World.BlockAccessor.GetClimateAt(ownerPos, EnumGetClimateMode.NowValues);
            float intensity = Math.Clamp(cond?.Rainfall ?? 0f, 0f, 1.5f);
            return Math.Clamp(WindowStorageLibConfig.Current.RainSoundVolumeValue * intensity, 0f, 2f);
        }

        private void TryDisposeSound(ILoadedSound sound)
        {
            if (sound == null) return;
            try
            {
                if (!sound.IsDisposed)
                {
                    sound.Stop();
                    sound.Dispose();
                }
            }
            catch (Exception e)
            {
                _be.Api?.Logger.Warning($"[WindowStorageLib] Error disposing sound: {e.Message}");
            }
        }
    }
}
