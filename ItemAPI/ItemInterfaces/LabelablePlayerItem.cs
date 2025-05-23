﻿using MonoMod.RuntimeDetour;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Alexandria.ItemAPI
{
    public interface ILabelItem
    {
        string GetLabel();
    }
    class LabelablePlayerItemSetup
    {
        /// <summary>
        /// Initialises the hooks necessary to make labelable player items functional.
        /// </summary>
        internal static void InitLabelHookInternal()
        {
            new Hook(
                typeof(GameUIItemController).GetMethod("UpdateItem", BindingFlags.Instance | BindingFlags.Public),
                typeof(LabelablePlayerItemSetup).GetMethod("UpdateCustomLabelHookInternal", BindingFlags.Static | BindingFlags.NonPublic)
            );
        }

        /// <summary>
        /// A hook method involved in making labelable player items functional.
        /// </summary>
        internal static void UpdateCustomLabelHookInternal(Action<GameUIItemController, PlayerItem, List<PlayerItem>> orig, GameUIItemController self, PlayerItem current, List<PlayerItem> items)
        {
            orig(self, current, items);
            if (current && current is ILabelItem)
            {
                var labelitem = (ILabelItem)current;
                var label = labelitem.GetLabel();
                if (!string.IsNullOrEmpty(label))
                {
                    self.ItemCountLabel.AutoHeight = true; // enable multiline text
                    self.ItemCountLabel.ProcessMarkup = true; // enable multicolor text
                    self.ItemCountLabel.IsVisible = true;
                    self.ItemCountLabel.Text = label;
                }
                else
                {
                    self.ItemCountLabel.IsVisible = false;
                }
            }            
        }

        [Obsolete("This method should never be called outside Alexandria and is public for backwards compatability only.", true)]
        public static void InitLabelHook() { }

        [Obsolete("This method should never be called outside Alexandria and is public for backwards compatability only.", true)]
        public static void UpdateCustomLabelHook(Action<GameUIItemController, PlayerItem, List<PlayerItem>> orig, GameUIItemController self, PlayerItem current, List<PlayerItem> items) { }
    }
}
