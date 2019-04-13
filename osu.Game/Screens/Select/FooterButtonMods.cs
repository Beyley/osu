﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Screens.Play.HUD;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.UI;
using System.Collections.Generic;
using osuTK;
using osu.Framework.Input.Events;

namespace osu.Game.Screens.Select
{
    public class FooterButtonMods : FooterButton
    {
        private readonly FooterModDisplay modDisplay;

        public FooterButtonMods(Bindable<IEnumerable<Mod>> mods)
        {
            Add(new Container
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Child = modDisplay = new FooterModDisplay {
                    DisplayUnrankedText = false,
                    Scale = new Vector2(0.8f)
                },
                AutoSizeAxes = Axes.Both,
                Margin = new MarginPadding { Left = 70 }
            });

            if (mods != null)
                modDisplay.Current = mods;
        }

        private class FooterModDisplay : ModDisplay
        {
            public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) => Parent?.Parent?.ReceivePositionalInputAt(screenSpacePos) ?? false;
        }
    }
}
