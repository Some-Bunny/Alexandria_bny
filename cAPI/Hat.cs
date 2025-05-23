﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Gungeon;
using Dungeonator;
using System.Reflection;
using Alexandria.ItemAPI;
using System.Collections;
using System.Globalization;
using HarmonyLib;

namespace Alexandria.cAPI
{
    public class Hat : BraveBehaviour
    {
        private const string LOCKED_TEXT     = "(LOCKED)";
        private const float BASE_FLIP_HEIGHT = 3f;

        private static tk2dSpriteAnimator CachedP1Animator = null;
        private static tk2dSpriteAnimator CachedP2Animator = null;

        public string            hatName                  = null;
        public Vector2           hatOffset                = Vector2.zero;
        public HatDirectionality hatDirectionality        = HatDirectionality.NONE;
        public HatRollReaction   hatRollReaction          = HatRollReaction.FLIP;
        public HatAttachLevel    attachLevel              = HatAttachLevel.HEAD_TOP;
        public string            flipStartedSound         = null;
        public string            flipEndedSound           = null;
        public HatDepthType      hatDepthType             = HatDepthType.ALWAYS_IN_FRONT;
        public float             flipSpeedMultiplier      = 1f;
        public float             flipHeightMultiplier     = 1f;
        public bool              goldenPedestal           = false;
        public bool              flipHorizontalWithPlayer = true;
        public string            unlockHint               = null;
        public bool              showSilhouetteWhenLocked = false;
        public bool              excludeFromHatRoom       = false;
        public OverridableBool   vanishOverrides          = new(false);

        public bool   HasBeenUnlocked => unlockPrereqs.All(p => p.CheckConditionsFulfilled());
        public string UnlockText      => string.IsNullOrEmpty(unlockHint) ? LOCKED_TEXT : $"{LOCKED_TEXT}\n\n{unlockHint}";

        internal bool                addedToHatabase      = false;
        internal bool                autoDetectFlipType   = false;

        private PlayerController     hatOwner             = null;
        private HatDirection         currentDirection     = HatDirection.NONE;
        private HatState             currentState         = HatState.SITTING;
        private tk2dSprite           hatSprite            = null;
        private tk2dSpriteAnimator   hatSpriteAnimator    = null;
        private tk2dSpriteAnimator   hatOwnerAnimator     = null;
        private tk2dSpriteDefinition cachedDef            = null;
        private float                rollLength           = 0.65f;
        private float                startRolTime         = 0.0f;
        private Vector2              playerSpecificOffset = Vector2.zero;
        private Vector2              playerFlippedOffset  = Vector2.zero;
        private Vector2              hatFlipOffset        = Vector2.zero;
        private int                  hatWidth             = 0;
        private bool                 ownerIsModdedChar    = false;
        private bool                 forwardMeansSouth    = false;
        private List<DungeonPrerequisite> unlockPrereqs   = new();
        private Dictionary<string, Hatabase.FrameOffset> offsetDict = null;

        public void AddUnlockOnFlag(GungeonFlags flag) =>
            unlockPrereqs.Add(new() { prerequisiteType = DungeonPrerequisite.PrerequisiteType.FLAG, saveFlagToCheck = flag });

        public void AddUnlockPrerequisite(DungeonPrerequisite prereq) => unlockPrereqs.Add(prereq);

        private void Start()
        {
            if (!hatOwner)
                return;
            hatSprite = base.GetComponent<tk2dSprite>();
            hatSpriteAnimator = base.GetComponent<tk2dSpriteAnimator>();
            SpriteOutlineManager.AddOutlineToSprite(hatSprite, Color.black, 1);

            // find and cache our player's animator
            hatOwnerAnimator = hatOwner.transform.Find("PlayerSprite").gameObject.GetComponent<tk2dSpriteAnimator>();
            if (hatOwner == GameManager.Instance.PrimaryPlayer)
                CachedP1Animator = hatOwnerAnimator;
            else if (hatOwner == GameManager.Instance.SecondaryPlayer)
                CachedP2Animator = hatOwnerAnimator;

            // get the player specific offset for the hat
            DeterminePlayerSpecificOffsets();

            // cache some useful variables
            hatWidth = Mathf.RoundToInt(16f * hatSprite.GetCurrentSpriteDef().colliderVertices[1].x);
            hatFlipOffset = hatOffset.WithX(-hatOffset.x);

            UpdateHatFacingDirection();
            HandleAttachedSpriteDepth();
        }

        /// <summary>Make sure Paradox's hat offsets update when the underlying animation library changes.</summary>
        [HarmonyPatch]
        private static class CharacterAnimationRandomizerHandleAnimationCompletedSwapPatch
        {
            [HarmonyPatch(typeof(CharacterAnimationRandomizer), nameof(CharacterAnimationRandomizer.HandleAnimationCompletedSwap))]
            static void Postfix(CharacterAnimationRandomizer __instance, tk2dSpriteAnimator arg1, tk2dSpriteAnimationClip arg2)
            {
                if (!__instance.m_player || !__instance.m_player.IsVisible || !__instance.m_animator || !__instance.m_animator.Library)
                    return;
                if (__instance.m_player.gameObject.GetComponent<HatController>() is not HatController hc || hc.CurrentHat is not Hat hat)
                    return;
                hat.DeterminePlayerSpecificOffsets();
            }
        }

        /// <summary>Due to CharAPI and possibly CCM mixing up animation names, we need to determine whether our "forward" sprites correspond to facing south or east.</summary>
        private void DetermineIfForwardMeansSouthOrEast()
        {
            if (hatOwner.spriteAnimator.GetClipByName("idle_forward") is not tk2dSpriteAnimationClip southClip)
            {
                Debug.Log($"  {hatOwner.gameObject.name} doesn't have an idle_forward animation for hat purposes");
                return;
            }
            tk2dSpriteAnimationFrame southFrame = southClip.frames[0];
            forwardMeansSouth = southFrame.spriteCollection.spriteDefinitions[southFrame.spriteId].name.Contains("forward");
        }

        private void DeterminePlayerSpecificOffsets()
        {
            bool onEyes = (attachLevel == HatAttachLevel.EYE_LEVEL);
            var headOffsets = onEyes ? Hatabase.EyeLevel : Hatabase.HeadLevel;
            DetermineIfForwardMeansSouthOrEast();
            ownerIsModdedChar = !headOffsets.TryGetValue(hatOwner.sprite.spriteAnimator.library.name, out playerSpecificOffset);
            if (!ownerIsModdedChar)
            {
                offsetDict = onEyes ? Hatabase.EyeFrameOffsets : Hatabase.HeadFrameOffsets;
                playerFlippedOffset = playerSpecificOffset;
                return;
            }

            HatUtility.LazyLoadModdedHatData();
            string lookupKey = hatOwner.gameObject.name;
            if (!headOffsets.TryGetValue(lookupKey, out playerSpecificOffset))
                lookupKey = lookupKey.Replace("(Clone)", ""); // try looking up without the "(Clone)" in our name, if we have it
            if (!headOffsets.TryGetValue(lookupKey, out playerSpecificOffset))
                playerSpecificOffset = onEyes ? Hatabase.defaultEyeLevelOffset : Hatabase.defaultHeadLevelOffset;

            var flippedOffsets = onEyes ? Hatabase.FlippedEyeLevel : Hatabase.FlippedHeadLevel;
            if (!flippedOffsets.TryGetValue(lookupKey, out playerFlippedOffset))
                playerFlippedOffset = playerSpecificOffset;

            var moddedFrameOffsets = onEyes ? Hatabase.ModdedEyeFrameOffsets : Hatabase.ModdedHeadFrameOffsets;
            if (!moddedFrameOffsets.TryGetValue(lookupKey, out offsetDict))
                offsetDict = onEyes ? Hatabase.EyeFrameOffsets : Hatabase.HeadFrameOffsets;
        }

        /// <summary>Patch for recalculating hat offsets when the player swaps costumes in the Breach</summary>
        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.SwapToAlternateCostume))]
        private class CostumeSwapHatFixerPatch
        {
            static void Postfix(PlayerController __instance)
            {
                if (__instance.GetComponent<HatController>() is not HatController hatController)
                    return;
                if (hatController.CurrentHat is not Hat hat)
                    return;
                hat.DeterminePlayerSpecificOffsets();
            }
        }

        private void Update()
        {
            if (!hatOwner)
                return;
            HandleVanish(); //Make the Hat vanish upon pitfall, or when the player rolls if the hat is VANISH type
            if (!base.sprite.renderer.enabled)
            {
                if (hatOwner.IsSlidingOverSurface)
                   StickHatToPlayer(hatOwner);
                return; // nothing else to do while invisible
            }

            if (currentState == HatState.SITTING)
                StickHatToPlayer(hatOwner);
            UpdateHatFacingDirection();
            HandleAttachedSpriteDepth();
            HandleFlip();
        }

        /// <summary>Preemptively move hat immediately after the player's sprite changes to avoid a 1-frame delay on hat offsets</summary>
        [HarmonyPatch(typeof(tk2dSpriteAnimator), nameof(tk2dSpriteAnimator.SetFrameInternal))]
        private class UpdateHatAnimationPatch
        {
            static void Prefix(tk2dSpriteAnimator __instance, int currFrame)
            {
                if (__instance != CachedP1Animator && __instance != CachedP2Animator)
                    return; // we are not a player's sprite animator, don't do anything
                if (__instance.transform.parent.gameObject.GetComponent<HatController>() is not HatController hatController)
                    return; // no hat controller, nothing to do
                if (hatController.CurrentHat is not Hat hat)
                    return; // no hat, nothing to do

                tk2dSpriteAnimationFrame frame = __instance.currentClip.frames[currFrame];
                tk2dSpriteDefinition def = frame.spriteCollection.spriteDefinitions[frame.spriteId];
                if (def == hat.cachedDef)
                    return; // sprite hasn't changed, so nothing to do

                hat.cachedDef = def; // cache the new sprite definition
                if (hat.hatOwner && hat.currentState == HatState.SITTING)
                {
                    hat.transform.position = hat.GetHatPosition(hat.hatOwner); // update the hat position in light of the new sprite definition
                    hat.UpdateHatFacingDirection();
                    hat.HandleAttachedSpriteDepth();
                }
            }
        }

        private bool ShouldBeVanished()
        {
            if (!hatOwner || !hatOwner.IsVisible || !hatOwner.sprite.renderer.enabled)
                return true;
            if (vanishOverrides.Value)
                return true;
            if (hatOwner.IsFalling || hatOwner.IsGhost)
                return true;
            if(!hatOwnerAnimator || hatOwnerAnimator.CurrentClip.name == "doorway" || hatOwnerAnimator.CurrentClip.name == "spinfall")
                return true;
            if (hatOwner.HasPickupID(436)) // 436 == Bloodied Scarf
                return true;
            if ((hatRollReaction == HatRollReaction.VANISH) && hatOwner.IsDodgeRolling)
                return true;
            if (hatOwner.IsSlidingOverSurface)
                return true;
            return false;
        }

		private void HandleVanish()
        {
            bool Visible = base.sprite.renderer.enabled;
            bool shouldBeVanished = ShouldBeVanished();

            if (shouldBeVanished)
            {
                base.transform.parent = null;
                base.sprite.renderer.enabled = false;
            }
            else
                base.sprite.renderer.enabled = true;

            if (!Visible && !shouldBeVanished)
            {
                if (hatOwner)
                    StickHatToPlayer(hatOwner);
                SpriteOutlineManager.AddOutlineToSprite(hatSprite, Color.black, 1);
                HandleAttachedSpriteDepth();
            }
            else if (Visible && shouldBeVanished)
                SpriteOutlineManager.RemoveOutlineFromSprite(hatSprite);
        }

		private void UpdateHatFacingDirection()
        {
            HatDirection targetDir = FetchOwnerFacingDirection();
            if (targetDir == currentDirection)
                return; // nothing to update
            currentDirection = targetDir; // cache the actual targetDir rather than adjustedDir so we don't call this every frame unnecessarily

            // adjust the direction based on what our hat actually supports
            HatDirection adjustedDir = targetDir;
            if (hatDirectionality == HatDirectionality.NONE)
                adjustedDir = HatDirection.SOUTH;
            else if (hatDirectionality == HatDirectionality.TWO_WAY_HORIZONTAL)
                adjustedDir = (hatOwner && hatOwner.sprite.FlipX) ? HatDirection.WEST : HatDirection.EAST;
            else if (hatDirectionality == HatDirectionality.TWO_WAY_VERTICAL)
            {
                if (targetDir == HatDirection.NORTHWEST || targetDir == HatDirection.NORTHEAST || targetDir == HatDirection.NORTH)
                    adjustedDir = HatDirection.NORTH;
                else
                    adjustedDir = HatDirection.SOUTH;
            }
            else if (hatDirectionality == HatDirectionality.FOUR_WAY)
            {
                if (targetDir == HatDirection.NORTHWEST)
                    adjustedDir = HatDirection.WEST;
                else if (targetDir == HatDirection.NORTHEAST)
                    adjustedDir = HatDirection.EAST;
            }

            if (!hatSpriteAnimator) // can be null on very first frame of existence (potential problem with dynamically swapped hats)
            {
                hatSpriteAnimator = base.GetComponent<tk2dSpriteAnimator>();
                if (!hatSpriteAnimator)
                {
                    Debug.LogWarning("Failed to get hat animator in UpdateHatFacingDirection(), this really shouldn't happen...");
                    return;
                }
            }
            // pick the appropriate animation
            switch (adjustedDir)
            {
                case HatDirection.SOUTH:     { hatSpriteAnimator.Play("hat_south"); }     break;
                case HatDirection.NORTH:     { hatSpriteAnimator.Play("hat_north"); }     break;
                case HatDirection.WEST:      { hatSpriteAnimator.Play("hat_west"); }      break;
                case HatDirection.EAST:      { hatSpriteAnimator.Play("hat_east"); }      break;
                case HatDirection.NORTHWEST: { hatSpriteAnimator.Play("hat_northwest"); } break;
                case HatDirection.NORTHEAST: { hatSpriteAnimator.Play("hat_northeast"); } break;
                case HatDirection.NONE:
                    ETGModConsole.Log("ERROR: TRIED TO ROTATE HAT TO A NULL DIRECTION! (wtf?)");
                    break;
            }
        }

        private HatDirection GetBaseDirectionForSprite(string spriteName)
        {
            if (spriteName.Contains("front_right_")) return HatDirection.EAST;
            if (spriteName.Contains("right_front_")) return HatDirection.EAST;
            //HACK: charAPI mixed up sprite names and we can't change it now without breaking tons of CCs, so now we get to do this ._.
            if (spriteName.Contains("forward_"))     return forwardMeansSouth ? HatDirection.SOUTH : HatDirection.EAST;
            if (spriteName.Contains("back_right_"))  return HatDirection.NORTHEAST;
            if (spriteName.Contains("bright_"))      return HatDirection.NORTHEAST;
            if (spriteName.Contains("backwards_"))   return HatDirection.NORTHEAST;
            //HACK: charAPI compatibility AGAIN
            if (spriteName.Contains("backward_"))    return forwardMeansSouth ? HatDirection.NORTH : HatDirection.NORTHEAST;
            if (spriteName.Contains("bw_"))          return HatDirection.NORTHEAST;
            if (spriteName.Contains("north_"))       return HatDirection.NORTH;
            if (spriteName.Contains("up_"))          return HatDirection.NORTH; // only used by CharAPI characters
            if (spriteName.Contains("back_"))        return HatDirection.NORTH;
            if (spriteName.Contains("south_"))       return HatDirection.SOUTH;
            if (spriteName.Contains("front_"))       return HatDirection.SOUTH;
            return HatDirection.EAST; // return a sane default
        }

        private static readonly Dictionary<string, HatDirection> CachedSpriteDirections = new();
        private HatDirection FetchOwnerFacingDirection()
        {
            if (cachedDef == null)
                return HatDirection.EAST; // return a sane default if we're ownerless

            // figure out an approximate direction from the player's animation name
            if (!CachedSpriteDirections.TryGetValue(cachedDef.name, out HatDirection hatDir)) // Contains() is slow so cache the results as necessary
                hatDir = CachedSpriteDirections[cachedDef.name] = GetBaseDirectionForSprite(cachedDef.name);

            if (!hatOwner || !hatOwner.sprite.FlipX)
                return hatDir;
            if (hatDir == HatDirection.EAST)
                return HatDirection.WEST;
            if (hatDir == HatDirection.NORTHEAST)
                return HatDirection.NORTHWEST;

            return hatDir;
        }

        private static float GetDefOffset(tk2dSpriteDefinition def)
        {
            return def.boundsDataCenter.y + 0.5f * def.boundsDataExtents.y;
        }

		private Vector3 GetHatPosition(PlayerController player)
        {
            if (!hatSprite)
                return Vector3.zero; // can't do anything if our hat doesn't have a sprite yet

            cachedDef ??= player.sprite.GetCurrentSpriteDef();
            bool flipped = player.sprite.FlipX;

            // get the base offset for every character
            float effectiveX = player.SpriteBottomCenter.x;
            // due to weird rounding issues, we need to account for whether the player sprite and hat sprite are even / odd pixels and adjust the offset accordingly
            int playerWidth = Mathf.RoundToInt(16f * cachedDef.untrimmedBoundsDataExtents.x); // use untrimmed bounds to avoid missing pixels on alt skins
            if (flipped)
            {
                if (playerWidth % 2 == 0) // if our player sprite is an even number of pixels, we need to quantize our center point
                    effectiveX = effectiveX.Quantize(0.0625f, (hatWidth % 2 == 0) ? VectorConversions.Ceil : VectorConversions.Floor);
                if ((hatWidth + playerWidth) % 2 == 1) // if the sum of our player sprite width and hat sprite width is odd, we need to adjust by another half pixel
                    effectiveX += 1f/32f;
            }
            else
            {
                if ((hatWidth + playerWidth) % 2 == 1) // if the sum of our player sprite width and hat sprite width is odd, we need to adjust by another half pixel
                    effectiveX -= 1f/32f;
            }
            Vector2 baseOffset = new(effectiveX, player.sprite.transform.position.y);

            // get the animation frame specific offset, if one is available
            Vector2 animationFrameSpecificOffset = new(0, GetDefOffset(cachedDef));
            if (offsetDict.TryGetValue(HatUtility.GetSpriteBaseName(cachedDef.name), out Hatabase.FrameOffset frameOffset))
                animationFrameSpecificOffset += flipped ? frameOffset.flipOffset : frameOffset.offset;

            // combine everything and return
            return baseOffset // world position of top-center of player's sprite
                + animationFrameSpecificOffset  // offset for player's animation frame
                + ((flipped && flipHorizontalWithPlayer) ? hatFlipOffset : hatOffset) // offset for the hat itself
                + (flipped ? playerFlippedOffset : playerSpecificOffset); // offset for the player's head
        }

        internal void StickHatToPlayer(PlayerController player)
        {
            if (hatOwner == null)
                hatOwner = player;
            transform.parent = player.transform;
            transform.position = GetHatPosition(player);
            transform.rotation = hatOwner.transform.rotation;
            if (flipHorizontalWithPlayer && player.sprite)
                sprite.FlipX = player.sprite.FlipX;
            player.sprite.AttachRenderer(gameObject.GetComponent<tk2dBaseSprite>());
            currentState = HatState.SITTING;
        }

        private void HandleAttachedSpriteDepth()
        {
            const float FRONT_DEPTH = 0.01f;
            const float BACK_DEPTH = -0.6f;
            if (hatDepthType == HatDepthType.ALWAYS_IN_FRONT)
                hatSprite.HeightOffGround = FRONT_DEPTH;
            else if (hatDepthType == HatDepthType.ALWAYS_BEHIND)
                hatSprite.HeightOffGround = BACK_DEPTH;
            else
            {
                bool facingBack = (currentDirection == HatDirection.NORTH || currentDirection == HatDirection.NORTHEAST || currentDirection == HatDirection.NORTHWEST);
                if (hatDepthType == HatDepthType.BEHIND_WHEN_FACING_BACK)
    			    hatSprite.HeightOffGround = facingBack ? BACK_DEPTH :  FRONT_DEPTH;
                else
                    hatSprite.HeightOffGround = facingBack ?  FRONT_DEPTH : BACK_DEPTH;
            }
            hatSprite.UpdateZDepth();
        }

        /// <summary>Initialize hat flipping immediately after initiating a dodge roll</summary>
        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.StartDodgeRoll))]
        private class StartDodgeRolPatch
        {
            static void Postfix(PlayerController __instance, Vector2 direction, ref bool __result)
            {
                if (!__result || __instance.DodgeRollIsBlink)
                    return; // if we didn't start a dodge roll or we have a blink dodge roll, we can safely return
                if (__instance.GetComponent<HatController>() is not HatController hatCon)
                    return;
                if (hatCon.CurrentHat is not Hat hat)
                    return;
                hat.StartFlipping();
            }
        }

        private void StartFlipping()
        {
            if (GameManager.AUDIO_ENABLED && !string.IsNullOrEmpty(flipStartedSound))
                AkSoundEngine.PostEvent(flipStartedSound, gameObject);
            rollLength = hatOwner.rollStats.GetModifiedTime(hatOwner);
            currentState = HatState.FLIPPING;
            startRolTime = BraveTime.ScaledTimeSinceStartup;
        }

        private void HandleFlip()
        {
            if (BraveTime.DeltaTime == 0.0f)
                return; // don't do anything while time is frozen
            if (currentState != HatState.FLIPPING)
                return; // not flipping, so nothing to do
            if (hatRollReaction != HatRollReaction.FLIP || vanishOverrides.Value)
                return; // no flipping needed

            if (((BraveTime.ScaledTimeSinceStartup - startRolTime) >= rollLength) || hatOwner.IsSlidingOverSurface || !hatOwner.IsDodgeRolling)
            {
                StickHatToPlayer(hatOwner);
                if (GameManager.AUDIO_ENABLED && !string.IsNullOrEmpty(flipEndedSound))
                    AkSoundEngine.PostEvent(flipEndedSound, gameObject);
                return;
            }

            // logic for doing the actual flipping
            float rollAmount = 360f * (BraveTime.DeltaTime / rollLength);
            this.transform.RotateAround(this.sprite.WorldCenter, Vector3.forward, rollAmount * flipSpeedMultiplier * (hatOwner.sprite.FlipX ? 1f : -1f));
            float percentDone = (BraveTime.ScaledTimeSinceStartup - startRolTime) / rollLength;
            Vector3 flipOffset = new(0f, BASE_FLIP_HEIGHT * flipHeightMultiplier * Mathf.Sin(Mathf.PI * percentDone), 0f);
            this.transform.position = GetHatPosition(hatOwner) + flipOffset;
        }

        public enum HatDepthType
        {
            ALWAYS_IN_FRONT,
            ALWAYS_BEHIND,
            BEHIND_WHEN_FACING_BACK,
            IN_FRONT_WHEN_FACING_BACK
		}

        public enum HatDirectionality
        {
            NONE,
            TWO_WAY_HORIZONTAL,
            TWO_WAY_VERTICAL,
            FOUR_WAY,
            SIX_WAY,
        }

        public enum HatRollReaction
        {
            FLIP,
            VANISH,
            NONE,
        }

        public enum HatAttachLevel
        {
            HEAD_TOP,
            EYE_LEVEL,
        }

        public enum HatDirection
        {
            NORTH,
            SOUTH,
            WEST,
            EAST,
            NORTHWEST,
            NORTHEAST,
            NONE,
        }

        public enum HatState
        {
            SITTING,
            FLIPPING,
        }
	}
}
