// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;

namespace Q2JumpStarter
{
    // Fixed publisher identities; neither setup arguments nor release metadata
    // may introduce another repository, executable or ownership namespace.
    internal sealed class GameClient
    {
        internal static readonly GameClient Q2Pro = new GameClient("q2pro", "Q2PRO Race", "q2pro_race", "game", true);
        internal static readonly GameClient R1Q2 = new GameClient("r1q2", "R1Q2 Race", "r1q2_race", "r1q2", false);
        internal readonly string Id, Name, Repository, Executable, Component;
        internal readonly bool RequiresVisualCpp;
        private GameClient(string id, string name, string repository, string component, bool requiresVisualCpp)
        {
            Id = id; Name = name; Repository = "MovoWR/" + repository;
            Executable = repository + ".exe"; Component = component; RequiresVisualCpp = requiresVisualCpp;
        }
        internal string Origin { get { return "https://github.com/" + Repository; } }
        internal string ApiUrl { get { return "https://api.github.com/repos/" + Repository + "/releases/tags/latest"; } }
        internal string SourceApiUrl { get { return "https://api.github.com/repos/" + Repository + "/git/ref/tags/latest"; } }
        internal string AssetPattern { get { return this == Q2Pro ? @"\Aq2pro_race-windows-x64-r([0-9]{1,10})\.zip\z" :
            @"\Ar1q2_race-windows-x64-(?:r([0-9]{1,10})|([0-9]{1,10})ups)\.zip\z"; } }
        internal static GameClient ForComponent(string component)
        {
            if (component == Q2Pro.Component) return Q2Pro;
            if (component == R1Q2.Component) return R1Q2;
            throw new InvalidDataException("Unknown game client component.");
        }
        internal static GameClient[] Selection(string selection)
        {
            if (selection == "q2pro") return new[] { Q2Pro };
            if (selection == "r1q2") return new[] { R1Q2 };
            if (selection == "both") return new[] { Q2Pro, R1Q2 };
            throw new ArgumentException("Choose q2pro, r1q2 or both.");
        }
        internal static bool MetadataUrl(string url)
        { return url == Q2Pro.ApiUrl || url == Q2Pro.SourceApiUrl || url == R1Q2.ApiUrl || url == R1Q2.SourceApiUrl; }
    }
}
