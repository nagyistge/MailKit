﻿//
// ImapUtils.cs
//
// Author: Jeffrey Stedfast <jeff@xamarin.com>
//
// Copyright (c) 2014 Jeffrey Stedfast
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;

namespace MailKit.Net.Imap {
	/// <summary>
	/// IMAP utility functions.
	/// </summary>
	static class ImapUtils
	{
		static readonly string[] Months = {
			"Jan", "Feb", "Mar", "Apr", "May", "Jun",
			"Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
		};

		public static string FormatInternalDate (DateTimeOffset date)
		{
			return string.Format ("{0:D2}-{1}-{2:D4} {3:D2}:{4:D2}:{5:D2} {6:+00;-00}{7:00}",
				date.Day, Months[date.Month - 1], date.Year, date.Hour, date.Minute, date.Second,
				date.Offset.Hours, date.Offset.Minutes);
		}

		public static bool TryParseUidSet (string atom, out string[] uids)
		{
			var ranges = atom.Split (new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
			var list = new List<string> ();

			uids = null;

			for (int i = 0; i < ranges.Length; i++) {
				var minmax = ranges[i].Split (':');
				uint min;

				if (!uint.TryParse (minmax[0], out min) || min == 0)
					return false;

				if (minmax.Length == 2) {
					uint max;

					if (!uint.TryParse (minmax[1], out max) || max == 0)
						return false;

					for (uint uid = min; uid <= max; uid++)
						list.Add (uid.ToString ());
				} else if (minmax.Length == 1) {
					list.Add (minmax[0]);
				} else {
					return false;
				}
			}

			uids = list.ToArray ();

			return true;
		}

		public static string FormatUidSet (uint[] uids)
		{
			if (uids == null)
				throw new ArgumentNullException ("uids");

			var builder = new StringBuilder ();
			int index = 0;

			while (index < uids.Length) {
				uint min = uids[0];
				uint max = min;
				int i = 0;

				while (i < uids.Length) {
					if (uids[i] > max + 1)
						break;

					max = uids[i++];
				}

				if (builder.Length > 0)
					builder.Append (',');

				if (max > min)
					builder.AppendFormat ("{0}:{1}", min, max);
				else
					builder.Append (min.ToString ());

				index = i;
			}

			return builder.ToString ();
		}

		public static string FormatUidSet (int[] indexes)
		{
			if (indexes == null)
				throw new ArgumentNullException ("indexes");

			var uids = new uint[indexes.Length];

			for (int i = 0; i < indexes.Length; i++) {
				if (i < 0)
					throw new ArgumentException ("One or more indexes were negative.", "indexes");

				uids[i] = (uint) indexes[i] + 1;
			}

			return FormatUidSet (uids);
		}

		public static string FormatUidSet (string[] uids)
		{
			if (uids == null)
				throw new ArgumentNullException ("uids");

			var values = new uint[uids.Length];

			for (int i = 0; i < uids.Length; i++) {
				uint uid;

				if (!uint.TryParse (uids[i], out uid))
					throw new ArgumentException ("One or more uids were invalid.", "uids");

				values[i] = uid;
			}

			return FormatUidSet (values);
		}

		public static void HandleUntaggedListResponse (ImapEngine engine, ImapCommand ic, int index, ImapToken tok)
		{
			var token = engine.ReadToken (ic.CancellationToken);
			var list = (List<ImapFolder>) ic.UserData;
			var attrs = FolderAttributes.None;
			string encodedName;
			ImapFolder folder;
			char delim;

			// parse the folder attributes list
			if (token.Type != ImapTokenType.OpenParen)
				throw ImapEngine.UnexpectedToken (token, false);

			token = engine.ReadToken (ic.CancellationToken);

			while (token.Type == ImapTokenType.Flag || token.Type == ImapTokenType.Atom) {
				string atom = (string) token.Value;

				switch (atom.ToUpperInvariant ()) {
				case "\\NOINFERIORS":   attrs |= FolderAttributes.NoInferiors; break;
				case "\\NOSELECT":      attrs |= FolderAttributes.NoSelect; break;
				case "\\MARKED":        attrs |= FolderAttributes.Marked; break;
				case "\\UNMARKED":      attrs |= FolderAttributes.Unmarked; break;
				case "\\NONEXISTENT":   attrs |= FolderAttributes.NonExistent; break;
				case "\\SUBSCRIBED":    attrs |= FolderAttributes.Subscribed; break;
				case "\\REMOTE":        attrs |= FolderAttributes.Remote; break;
				case "\\HASCHILDREN":   attrs |= FolderAttributes.HasChildren; break;
				case "\\HASNOCHILDREN": attrs |= FolderAttributes.HasNoChildren; break;
				case "\\ALL":           attrs |= FolderAttributes.All; break;
				case "\\ARCHIVE":       attrs |= FolderAttributes.Archive; break;
				case "\\DRAFTS":        attrs |= FolderAttributes.Drafts; break;
				case "\\FLAGGED":       attrs |= FolderAttributes.Flagged; break;
				case "\\JUNK":          attrs |= FolderAttributes.Junk; break;
				case "\\SENT":          attrs |= FolderAttributes.Sent; break;
				case "\\TRASH":         attrs |= FolderAttributes.Trash; break;
				}

				token = engine.ReadToken (ic.CancellationToken);
			}

			if (token.Type != ImapTokenType.CloseParen)
				throw ImapEngine.UnexpectedToken (token, false);

			// parse the path delimeter
			token = engine.ReadToken (ic.CancellationToken);

			if (token.Type == ImapTokenType.QString) {
				var qstring = (string) token.Value;

				delim = qstring[0];
			} else if (token.Type == ImapTokenType.Nil) {
				delim = '\0';
			} else {
				throw ImapEngine.UnexpectedToken (token, false);
			}

			// parse the folder name
			token = engine.ReadToken (ic.CancellationToken);

			switch (token.Type) {
			case ImapTokenType.Literal:
				encodedName = engine.ReadLiteral (ic.CancellationToken);
				break;
			case ImapTokenType.QString:
			case ImapTokenType.Atom:
				encodedName = (string) token.Value;
				break;
			default:
				throw ImapEngine.UnexpectedToken (token, false);
			}

			// skip any remaining tokens...
			engine.SkipLine (ic.CancellationToken);

			if (engine.FolderCache.TryGetValue (encodedName, out folder)) {
				folder.Attributes = (folder.Attributes & ~(FolderAttributes.Marked | FolderAttributes.Unmarked)) | attrs;
			} else {
				folder = new ImapFolder (engine, encodedName, attrs, delim);
				engine.FolderCache.Add (encodedName, folder);
			}

			list.Add (folder);
		}

		public static void LookupParentFolders (ImapEngine engine, IEnumerable<ImapFolder> folders, CancellationToken cancellationToken)
		{
			int index;

			foreach (var folder in folders) {
				if (folder.ParentFolder != null)
					continue;

				if ((index = folder.FullName.LastIndexOf (folder.DirectorySeparator)) == -1)
					continue;

				if (index == 0)
					continue;

				var parentName = folder.FullName.Substring (0, index);
				var encodedName = ImapEncoding.Encode (parentName);
				ImapFolder parent;

				if (engine.FolderCache.TryGetValue (encodedName, out parent)) {
					folder.ParentFolder = parent;
					continue;
				}

				var ic = engine.QueueCommand (cancellationToken, null, "LIST \"\" %S\r\n", encodedName);
				ic.RegisterUntaggedHandler ("LIST", ImapUtils.HandleUntaggedListResponse);
				ic.UserData = new List<ImapFolder> ();

				engine.Wait (ic);

				if (!engine.FolderCache.TryGetValue (encodedName, out parent)) {
					parent = new ImapFolder (engine, encodedName, FolderAttributes.NonExistent, folder.DirectorySeparator);
					engine.FolderCache.Add (encodedName, parent);
				}

				folder.ParentFolder = parent;
			}
		}

		public static string FormatFlagsList (MessageFlags flags)
		{
			var builder = new StringBuilder ();

			builder.Append ('(');
			if ((flags & MessageFlags.Answered) != 0)
				builder.Append ("\\Answered ");
			if ((flags & MessageFlags.Deleted) != 0)
				builder.Append ("\\Deleted ");
			if ((flags & MessageFlags.Draft) != 0)
				builder.Append ("\\Draft ");
			if ((flags & MessageFlags.Flagged) != 0)
				builder.Append ("\\Flagged ");
			if ((flags & MessageFlags.Seen) != 0)
				builder.Append ("\\Seen ");
			if (builder.Length > 1)
				builder.Length--;
			builder.Append (')');

			return builder.ToString ();
		}

		public static MessageFlags ParseFlagsList (ImapEngine engine, CancellationToken cancellationToken)
		{
			var token = engine.ReadToken (cancellationToken);
			var flags = MessageFlags.None;

			if (token.Type != ImapTokenType.OpenParen) {
				Debug.WriteLine ("Expected '(' at the start of the flags list, but got: {0}", token);
				throw ImapEngine.UnexpectedToken (token, false);
			}

			token = engine.ReadToken (cancellationToken);

			while (token.Type == ImapTokenType.Atom || token.Type == ImapTokenType.Flag) {
				string flag = (string) token.Value;
				switch (flag) {
				case "\\Answered": flags |= MessageFlags.Answered; break;
				case "\\Deleted":  flags |= MessageFlags.Deleted; break;
				case "\\Draft":    flags |= MessageFlags.Draft; break;
				case "\\Flagged":  flags |= MessageFlags.Flagged; break;
				case "\\Seen":     flags |= MessageFlags.Seen; break;
				case "\\Recent":   flags |= MessageFlags.Recent; break;
				case "\\*":        flags |= MessageFlags.UserDefined; break;
				}

				token = engine.ReadToken (cancellationToken);
			}

			if (token.Type !=  ImapTokenType.CloseParen) {
				Debug.WriteLine ("Expected to find a ')' token terminating the flags list, but got: {0}", token);
				throw ImapEngine.UnexpectedToken (token, false);
			}

			return flags;
		}
	}
}
