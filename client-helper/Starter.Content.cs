// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Q2JumpStarter
{
    internal static class ContentService
    {
        internal static string SafePath(string root, string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || relative.Contains(":") || relative.Contains("\\") || relative.StartsWith("/") || relative.Split('/').Any(p => p.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(p, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase) || p == "" || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ")))
                throw new InvalidDataException("Unsafe content path.");
            string prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(prefix, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Content escapes the game folder.");
            string parent = path;
            while (parent != null)
            { if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Content paths cannot use symbolic links or junctions."); parent = System.IO.Path.GetDirectoryName(parent); }
            return path;
        }
        internal static string Hash(string path, CancellationToken token = default(CancellationToken))
        { using (var stream = File.OpenRead(path)) return Hash(stream, token); }
        internal static string Hash(Stream stream, CancellationToken token)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[65536]; int count;
                while (true)
                {
                    token.ThrowIfCancellationRequested(); count = stream.Read(buffer, 0, buffer.Length);
                    token.ThrowIfCancellationRequested(); if (count == 0) break;
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
