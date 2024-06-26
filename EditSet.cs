﻿using System;
using System.Collections.Generic;
using static WebOne.Program;

namespace WebOne
{
	/// <summary>
	/// Set of edits for particular pages
	/// </summary>
	class EditSet
	{
		/// <summary>
		/// List of masks of URLs on which the Set whould be used [title and OnUrl]
		/// </summary>
		public List<string> UrlMasks { get; set; }

		/// <summary>
		/// List of masks of URLs on which the Set would not be used [IgnoreUrl]
		/// </summary>
		public List<string> UrlIgnoreMasks { get; set; }

		/// <summary>
		/// List of masks of MIME Content-Types on which the Set would be used [OnContentType]
		/// </summary>
		public List<string> ContentTypeMasks { get; set; }

		/// <summary>
		/// Mask (exact) of HTTP status code where the Set would be used [OnCode]
		/// </summary>
		public int? OnCode { get; set; }

		/// <summary>
		/// Flag indicating that the edit set should be used only on plain HTTP requests
		/// </summary>
		public bool HttpOnly { get; set; }

		/// <summary>
		/// Flag indicating that the edit set should be used only on HTTPS requests
		/// </summary>
		public bool HttpsOnly { get; set; }

		/// <summary>
		/// List of masks of HTTP request headers on which the Set would not be used [OnHeader]
		/// </summary>
		public List<string> HeaderMasks { get; set; }

		/// <summary>
		/// Flag that indicates that the edits can be performed at time of HTTP Request (before get of response)
		/// </summary>
		public bool IsForRequest { get; private set; }

		/// <summary>
		/// Limitation by proxy server operating system [OnHostOS]<br/>
		/// True if OS is good, False if is bad.
		/// </summary>
		public bool CorrectHostOS { get; private set; }

		/// <summary>
		/// List of edits that would be performed on the need content
		/// </summary>
		public List<EditSetRule> Edits { get; set; }

		/// <summary>
		/// Create a Set of edits from a section from webone.conf
		/// </summary>
		/// <param name="Section">The webone.conf section</param>
		public EditSet(ConfigFileSection Section)
		{
			UrlMasks = new List<string>();
			UrlIgnoreMasks = new List<string>();
			ContentTypeMasks = new List<string>();
			HeaderMasks = new List<string>();
			CorrectHostOS = true;
			Edits = new List<EditSetRule>();
			IsForRequest = false;

			bool MayBeForResponse = false; //does this set containing tasks for HTTP response processing?


			if (Section.Mask != null)
			{
				Program.CheckRegExp(Section.Mask, Section.Location);
				UrlMasks.Add(Section.Mask);
			}

			foreach (var Line in Section.Options)
			{
				if (!Line.HaveKeyValue) continue;
				switch (Line.Key)
				{
					//detection rules
					case "OnUrl":
						CheckRegExp(Line);
						UrlMasks.Add(Line.Value);
						continue;
					case "OnCode":
						OnCode = int.Parse(Line.Value);
						continue;
					case "IgnoreUrl":
						CheckRegExp(Line);
						UrlIgnoreMasks.Add(Line.Value);
						continue;
					case "OnContentType":
						CheckRegExp(Line);
						ContentTypeMasks.Add(Line.Value);
						continue;
					case "OnHeader":
						CheckRegExp(Line);
						HeaderMasks.Add(Line.Value);
						continue;
					case "OnHostOS":
						switch (Line.Value.ToLower())
						{
							case "windows":
								CorrectHostOS = OperatingSystem.IsWindows();
								continue;
							case "linux":
								CorrectHostOS = OperatingSystem.IsLinux();
								continue;
							case "macos":
								CorrectHostOS = OperatingSystem.IsMacOS();
								continue;
							default:
								new LogWriter().WriteLine(true, false, "Warning: unknown host OS \"{0}\", edit set disabled.", Line.Value);
								CorrectHostOS = false;
								continue;
						}
					case "OnHttpOnly":
						HttpOnly = ToBoolean(Line.Value);
						continue;
					case "OnHttpsOnly":
						HttpsOnly = ToBoolean(Line.Value);
						continue;
					//editing rules (can contain regular expressions)
					case "AddRedirect":
					case "AddInternalRedirect":
					case "AddFind":
					case "AddReplace":
						CheckRegExp(Line);
						Edits.Add(new EditSetRule(Line.Key, Line.Value));
						break;
					//editing rules (cannot contain regular expressions)
					case "AddHeader":
					case "AddResponseHeader":
					case "AddConvert":
					case "AddConvertDest":
					case "AddConvertArg1":
					case "AddConvertArg2":
					case "AddRequestHeaderFind":
					case "AddRequestHeaderReplace":
					case "AddResponseHeaderFind":
					case "AddResponseHeaderReplace":
					case "AddHeaderDumping":
					case "AddRequestDumping":
					case "AddDumping":
					case "AddOutputEncoding":
					case "AddTranslit":
						Edits.Add(new EditSetRule(Line.Key, Line.Value));
						break;
					default:
						if (Line.Key.StartsWith("Add"))
							new LogWriter().WriteLine(true, false, "Warning: unknown editing rule \"{0}\".", Line.Key);
						else
							new LogWriter().WriteLine(true, false, "Warning: unknown detection rule \"{0}\".", Line.Key);
						break;
				}
				if (Line.Key.StartsWith("AddConvert")) MayBeForResponse = true;
				if (Line.Key == "AddContentType") MayBeForResponse = true;
				if (Line.Key == "AddFind") MayBeForResponse = true;
				if (Line.Key == "AddReplace") MayBeForResponse = true;
				if (Line.Key == "AddInternalRedirect") MayBeForResponse = false;
			}

			ProcessComplexRules(Section.Location);

			//check if the edit set can be runned on HTTP-request time
			if (ContentTypeMasks.Count == 0 && !MayBeForResponse) IsForRequest = true;

			if (UrlMasks.Count == 0) UrlMasks.Add(".*");
		}

		/// <summary>
		/// Process all multi-line rules (like AddFind+AddReplace) to virtual rules (e.g. AddFindReplace)
		/// </summary>
		/// <param name="EditSetLocation">Edit Set's location (for error message if need)</param>
		private void ProcessComplexRules(string EditSetLocation)
		{
			/* List of virtual editing rules, not listed at https://github.com/atauenis/webone/wiki/Sets-of-edits

             * AddFind + AddReplace = AddFindReplace                                             (FindReplaceEditSetRule)
             * AddConvert + AddConvertDest + AddConvertArg1 + AddConvertArg2 = AddConverting     (ConvertEditSetRule)
             * AddHeaderFind + AddHeaderReplace = AddRequestHeaderFindReplace                    (FindReplaceEditSetRule)
             * AddResponseHeaderFind + AddResponseHeaderReplace = AddResponseHeaderFindReplace   (FindReplaceEditSetRule)
             */

			//load all original lines
			List<string> Finds = new();
			List<string> Replacions = new();
			List<string> RequestHeaderFinds = new();
			List<string> RequestHeaderReplacions = new();
			List<string> ResponseHeaderFinds = new();
			List<string> ResponseHeaderReplacions = new();
			string Converter = null;
			string ConvertDest = "";
			string ConvertArg1 = "";
			string ConvertArg2 = "";
			foreach (EditSetRule Rule in Edits)
			{
				switch (Rule.Action)
				{
					case "AddFind":
						Finds.Add(Rule.Value);
						break;
					case "AddReplace":
						Replacions.Add(Rule.Value);
						break;
					case "AddRequestHeaderFind":
						RequestHeaderFinds.Add(Rule.Value);
						break;
					case "AddRequestHeaderReplace":
						RequestHeaderReplacions.Add(Rule.Value);
						break;
					case "AddResponseHeaderFind":
						ResponseHeaderFinds.Add(Rule.Value);
						break;
					case "AddResponseHeaderReplace":
						ResponseHeaderReplacions.Add(Rule.Value);
						break;
					case "AddConvert":
						Converter = Rule.Value;
						break;
					case "AddConvertDest":
						ConvertDest = Rule.Value;
						break;
					case "AddConvertArg1":
						ConvertArg1 = Rule.Value;
						break;
					case "AddConvertArg2":
						ConvertArg2 = Rule.Value;
						break;
				}
			}

			//process AddFind, AddReplace -> AddFindReplace
			if (Finds.Count != Replacions.Count)
				Log.WriteLine(true, false, "Warning: Invalid amount of finds/replaces in {0}.", EditSetLocation);
			else if (Finds.Count > 0)
				for (int i = 0; i < Finds.Count; i++)
				{
					Edits.Add(new FindReplaceEditSetRule("AddFindReplace", Finds[i], Replacions[i]));
				}

			//process AddHeaderFind, AddHeaderReplace -> AddRequestHeaderFindReplace
			if (RequestHeaderFinds.Count != RequestHeaderReplacions.Count)
				Log.WriteLine(true, false, "Warning: Invalid amount of request header finds/replaces in {0}.", EditSetLocation);
			else if (RequestHeaderFinds.Count > 0)
				for (int i = 0; i < RequestHeaderFinds.Count; i++)
				{
					Edits.Add(new FindReplaceEditSetRule("AddRequestHeaderFindReplace", RequestHeaderFinds[i], RequestHeaderReplacions[i]));
				}

			//process AddResponseHeaderFind, AddResponseHeaderReplace -> AddResponseHeaderFindReplace
			if (ResponseHeaderFinds.Count != ResponseHeaderReplacions.Count)
				Log.WriteLine(true, false, "Warning: Invalid amount of response header finds/replaces in {0}.", EditSetLocation);
			else if (ResponseHeaderFinds.Count > 0)
				for (int i = 0; i < ResponseHeaderFinds.Count; i++)
				{
					Edits.Add(new FindReplaceEditSetRule("AddResponseHeaderFindReplace", ResponseHeaderFinds[i], ResponseHeaderReplacions[i]));
				}

			//process AddConvert, AddConvertDest, AddConvertArg1, AddConvertArg2 -> AddConverting
			if (!string.IsNullOrEmpty(Converter))
			{
				//check converter presence and warn if there is no such
				bool CorrectConverter = false;
				foreach (var conv in ConfigFile.Converters)
				{
					if (conv.Executable == Converter)
					{
						Edits.Add(new ConvertEditSetRule("AddConverting", Converter, ConvertDest, ConvertArg1, ConvertArg2));
						CorrectConverter = true;
						break;
					}
				}
				if (!CorrectConverter) Log.WriteLine(true, false, @"Warning: Converter ""{1}"" in Edit Set at {0} is not present in lists of converters.", EditSetLocation, Converter);

			}
			else if (ConvertDest != "" || ConvertArg1 != "" || ConvertArg2 != "")
			{
				Log.WriteLine(true, false, "Warning: Please add AddConvert rule to Edit Set starting at {0} to use converting.", EditSetLocation);
			}
		}

		/// <summary>
		/// Test validness of a Regular Expression pattern in a rule line
		/// </summary>
		/// <param name="RegExpLine">Rule line</param>
		/// <returns><see cref="True"/> if RegExp is valid or <see cref="False"/> if it is invalid</returns>
		private bool CheckRegExp(ConfigFileOption RegExpLine) { return Program.CheckRegExp(RegExpLine.Value, RegExpLine.Location); }

		//test function
		public override string ToString()
		{
			string Str = "[Edit:" + UrlMasks[0] + "]\n";
			if (UrlMasks.Count > 1) for (int i = 1; i < UrlMasks.Count; i++) Str += "OnUrl=" + UrlMasks[i] + "\n";
			foreach (var imask in UrlIgnoreMasks) Str += "IgnoreUrl=" + "=" + imask + "\n";
			foreach (var ctmask in ContentTypeMasks) Str += "OnContentType=" + "=" + ctmask + "\n";
			foreach (var hmask in HeaderMasks) Str += "OnHeader=" + "=" + hmask + "\n";
			foreach (var edit in Edits) Str += edit.Action + "=" + edit.Value + "\n";
			return Str;
		}
	}

}
