// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Q2JumpStarter
{
    public sealed class InstallationStatus
    {
        public bool CanPlay { get; internal set; }
        public string Message { get; internal set; }
        public string StarterVersion;
        public string ClientIdentity { get; internal set; }
        public int? ClientRevision { get; internal set; }
    }
}
