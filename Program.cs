using System;
using System.Collections.Generic;
using System.Xml;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
//-------------------------------------
using Newtonsoft.Json.Linq;
using Mono.Unix;

namespace plexbif {
   public enum Category {
      Movie,
      TV
   }
   class Program {
      private const string username = "REPLACE ME: YOUR PLEX USERNAME";
      private const string password = "REPLACE ME: YOUR PLEX PASSWORD";

      private const string SectionUrl = "http://127.0.0.1:32400/library/sections";
      private static string _token = null;
      static void Main (string[] args) {
#if DEBUG
         args = new string[] { "--clean" };
#endif
         if (args.Length > 0 && args[0] == "--help") {
            ShowUsage ();
            return;
         }
         bool displayMissing = args.Length > 0 && args[0] == "--showmissing";
         bool displayUnprocessed = args.Length > 0 && args[0] == "--showunproc";
         bool clean = args.Length > 0 && args[0] == "--clean";
         bool single = args.Length > 0 && args[0] == "--single";
         bool define = args.Length > 0 && args[0] == "--define";

         if (define) {
            Console.WriteLine (@"
Unprocessed:   Indexes have not been processed meaning plex's database does not have an entry for the index.

Missing Index: Gets a value indicating that the plex database index entry is missing but the physical index exists.
               Possible reasons plex hasn't processed the video file.

Missing Local: Index file where video file resides is missing.");
            return;
         }


         if (args.Length > 0 && displayMissing == false && displayUnprocessed == false && clean == false && single == false) {
            ShowUsage ();
            return;
         }

         if (clean) {
#if DEBUG
            string line = "Y";
#else
            Console.Write("Are you sure you want to clean (this can't be undone) (Y/N): ");
            string line = Console.ReadLine();
#endif
            if (line == "Y") {
               Console.WriteLine ("Cleaning...");
               string path = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.Personal), "Library", "Application Support", "Plex Media Server", "Media", "localhost");

               string[] bifs = Directory.GetFiles (path, "*.bif", SearchOption.AllDirectories);

               foreach (string bifFile in bifs) {
                  UnixSymbolicLinkInfo info = new UnixSymbolicLinkInfo (bifFile);

                  if (info.Exists && (!info.IsSymbolicLink || !info.GetContents ().Exists)) {
                     if (info.IsSymbolicLink) {
                        Console.WriteLine ("Deleting index file that points to {0}.", Path.GetFileName (info.GetContents ().FullName));
                     } else {
                        Console.WriteLine ("Deleting Index File: {0}", bifFile);
                     }
                     File.Delete (bifFile);
                  }
               }

               Console.WriteLine ("Operation Complete.");
            } else {
               Console.WriteLine ("Operation Aborted.");
            }

            return;
         }

         bool findHash = args.Length > 0 && args[0] == "--find";
         string hash = null;

         if (findHash && args.Length == 2) {
            hash = args[1];
         }

         JObject proxyResult = JsonProxy (null, "post", null, username, password);

         if (proxyResult["Token"] == null) {
            if (proxyResult["errStr"] != null) {
               Console.WriteLine (proxyResult["errStr"]);
               return;
            } else {
               Console.WriteLine ("Unknown Error");
               return;
            }
         }

         _token = (string)proxyResult["Token"];

         List<Section> sections = GetPlexSections ();

         if (single) {
            int itemIndex = 1;
            foreach (Section section in sections) {
               try {
                  Console.WriteLine ("{0}. {1}", itemIndex, section.Name);
               } finally {
                  itemIndex += 1;
               }
            }
            Console.Write ("Please select which library to process (1-{0}): ", itemIndex - 1);
            Regex numberFinder = new Regex ("(\\d+)");
            string inputLine = Console.ReadLine ();
            Match inputMatch = numberFinder.Match (inputLine);

            if (inputMatch.Success) {
               int selectedSection = int.Parse (inputMatch.Groups[1].Value) - 1;
               if (selectedSection < 0 || selectedSection >= sections.Count) {
                  Console.WriteLine ("Input out of range.");
                  return;
               }
               List<Section> newSections = new List<Section> ();
               newSections.Add (sections[selectedSection]);
               sections = newSections;
            } else {
               Console.WriteLine ("Input not recognized.");
               return;
            }
         }

         Dictionary<Section, List<MediaEntry>> items = new Dictionary<Section, List<MediaEntry>> ();
         long count = 0;
         uint percent = 0;
         uint nextPercent = 10;

         foreach (Section section in sections) {
            // if (!single) {
            //    switch (section.Name) {
            //       default:
            //          continue;
            //    }
            // }
            Console.WriteLine ("\nProcessing {0} Section", section.Name);
            if (section.Type == Category.Movie) {
               long max = -1;
               count = 0;
               percent = 0;
               nextPercent = 10;

               items.Add (section, GetMovieItems (section.Url, ref max, ref count, ref percent, ref nextPercent));
               if (findHash) {
                  foreach (MediaEntry entry in items[section]) {
                     if (entry.Hash.ToLower () == hash.ToLower ()) {
                        Console.WriteLine ("Found Entry {0} at {1}", entry.Title, entry.FilePath);
                        return;
                     }
                  }
               }
            } else {
#if DEBUG
               continue;
#else
               items.Add(section, GetTVShowItems(section.Url));
               if (findHash) {
                  foreach (MediaEntry entry in items[section]) {
                     if (entry.Hash.ToLower() == hash.ToLower()) {
                        Console.WriteLine("Found Entry {0} at {1}", entry.Title, entry.FilePath);
                        return;
                     }
                  }
               }
#endif
            }
         }


         bool missing = false;
         foreach (KeyValuePair<Section, List<MediaEntry>> sectionPair in items) {
            sectionPair.Key.Missing = sectionPair.Value.FindAll (m => m.MissingPossibleEntry).Count; //Plex database entry missing

            List<MediaEntry> needsProcessing = sectionPair.Value.FindAll(m => (!m.IndexProcessed)); //Plex has built the index file
            sectionPair.Key.NeedsProcessing = needsProcessing.Count;

            List<MediaEntry> missingLocalItems = sectionPair.Value.FindAll(m => m.MissingEntry); //Physical Location missing index file
            int missingLocal = missingLocalItems.Count;

            if (displayMissing) {
               foreach (MediaEntry entry in missingLocalItems) {
                  Console.WriteLine (entry.FilePath);
               }
            }

            if (displayUnprocessed) {
               foreach (MediaEntry entry in needsProcessing) {
                  Console.WriteLine (entry.FilePath);
               }
            }

            if (sectionPair.Key.Missing > 0) {
               missing = true;
            }

            Console.WriteLine ("Section {0}, Unprocessed {5} {6}, Missing {1} {2}, Missing {3} Local {4}",
               /* 0 */sectionPair.Key.Name,
               /* 1 */sectionPair.Key.Missing,
               /* 2 */sectionPair.Key.Missing > 1 ? "Indices" : "Index",
               /* 3 */missingLocal,
               /* 4 */missingLocal > 1 ? "Indices" : "Index",
               /* 5 */sectionPair.Key.NeedsProcessing,
               /* 6 */sectionPair.Key.NeedsProcessing > 1 ? "Indices" : "Index"
            );
         }

         if ((displayMissing || displayUnprocessed) && missing) {
            return;
         }

         if (findHash) {
            Console.WriteLine ("Hash Not Found...");
            return;
         }

         if (!missing) {
            Console.WriteLine ("Found Nothing to Add, Aborting Opertion...");
            return;
         }

         Console.Write ("Do you want to continue? (Y/N): ");
         string result = Console.ReadLine();

         if (result.ToUpper ()[0] != 'Y') {
            Console.WriteLine ("Operation was aborted...");
            return;
         }

         //            Console.WriteLine("Opening Plex Indexer...");
         //            OpenIndexer();
         //            Console.WriteLine("Plex Indexer Is Open...");

         int sectionCount = 0;
         int sectionMax = new List<Section>(items.Keys).FindAll(m => m.Missing > 0).Count;
         foreach (KeyValuePair<Section, List<MediaEntry>> sectionPair in items) {
            sectionCount++;
            count = 0;
            int max = sectionPair.Key.Missing;
            if (sectionPair.Key.Missing == 0) {
               continue;
            }

            Console.WriteLine ("Processing Section {0} ({1} of {2})...", sectionPair.Key.Name, sectionCount, sectionMax);

            foreach (MediaEntry entry in sectionPair.Value) {
               if (entry.MissingPossibleEntry) {
                  count++;
                  Console.WriteLine ("Processing {0} ({1} of {2})...", Path.GetFileNameWithoutExtension (entry.FilePath), count, max);
                  entry.Analyze ();
               }
            }
         }
         Console.WriteLine ("Indexes are in place, please wait for plex's scheduled task to process them.");
         //            Console.WriteLine("Closing Plex Indexer...");
         //            CloseIndexer();
         //            Console.WriteLine("Plex Indexer Is Closed...");
      }

      static void ShowUsage () {
         Console.WriteLine ("Usage: plexbif --<options>");
         Console.WriteLine (" single      - Process a single library");
         Console.WriteLine (" showmissing - Display missing local entry");
         Console.WriteLine (" showunproc  - Display items that are valid but not in the plex database");
         Console.WriteLine (" find <hash> - Find hash in plex database");
         Console.WriteLine (" clean       - Clean non-linked bif files");
      }

      private const string logonURL = "https://plex.tv/users/sign_in.json";

      private static JObject JsonProxy (string url, string method, string token = null, string username = null, string password = null) {
         ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls; //cdj DE700

         HttpWebRequest req = (HttpWebRequest) WebRequest.Create(url == null ? logonURL : url);

         if (token == null) {
            req.Accept = "application/json";
         }
         if (req.Headers != null) {
            if (token == null) {
               req.Headers["Authorization"] = "Basic " + Convert.ToBase64String (Encoding.ASCII.GetBytes (String.Format ("{0}:{1}", username, password)));
            } else {
               req.Headers["X-Plex-Token"] = token;
            }
            req.Headers["X-Plex-Client-Identifier"] = "pbc1_script";
            req.Headers["X-Plex-Product"] = "Plex Bif Local Processor";
            req.Headers["X-Plex-Version"] = "V1";

         }
         req.Method = method;

         try {
            if (method.ToLower () == "post") {
               //byte[] byteArray = Encoding.UTF8.GetBytes(data);
               // Set the ContentType property of the WebRequest.
               //req.ContentType = "application/json; charset=utf-8";
               // Set the ContentLength property of the WebRequest.
               //req.ContentLength = byteArray.Length;

               // Get the request stream.
               //Stream dataStream = req.GetRequestStream();
               // Write the data to the request stream.
               //dataStream.Write(byteArray, 0, byteArray.Length);
               // Close the Stream object.
               //dataStream.Close();
            }

            // Get the response.
            WebResponse resp = req.GetResponse();
            string retStr;
            using (Stream s = resp.GetResponseStream ()) {
               if (s == null) {
                  return null;
               }
               StreamReader rdr;
               using (rdr = new StreamReader (s)) {
                  retStr = rdr.ReadToEnd ();
               }
            }



            if (String.IsNullOrWhiteSpace (retStr)) {
               return new JObject {
                  { "Success", ((string)resp.Headers["Status"]).StartsWith("204") }
               };
            } else {
               if (token == null) {
                  JObject result = JObject.Parse(retStr);
                  return new JObject {
                     { "Token", result["user"]["authentication_token"] }
                  };
               } else {
                  return new JObject {
                     { "Result", retStr }
                  };
               }

            }

         } catch (WebException e) {
            if ((e.Status == WebExceptionStatus.ProtocolError & e.Response != null)) {
               HttpWebResponse webResp = (HttpWebResponse) e.Response;
               if (webResp.StatusCode == HttpStatusCode.Unauthorized) {
                  return new JObject { { "errStr", "The Plex credentials were invalid." } };
               }
            }
            return new JObject { { "errStr", e.Message } };
         }
      }

      private static TextReader ReadXmlFromURL (string url) {
         bool retry = true;
         string retVal = null;
         while (retry) {
            JObject result = JsonProxy(url, "get", _token);

            if (result["Result"] != null) {
               retVal = (string)result["Result"];

               retry = false;
            }
         }

         return new StringReader (retVal);
      }


      private static void OpenIndexer () {
         HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:32400/:/prefs?GenerateIndexFilesDuringAnalysis=1");
         request.Method = "PUT";
         request.GetRequestStream ().Close ();
         request.GetResponse ();

         request = (HttpWebRequest)WebRequest.Create ("http://127.0.0.1:32400/:/prefs?GenerateBIFBehavior=asap");
         request.Method = "PUT";
         request.GetRequestStream ().Close ();
         request.GetResponse ();
      }

      private static void CloseIndexer () {
         HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:32400/:/prefs?GenerateIndexFilesDuringAnalysis=0");
         request.Method = "PUT";
         request.GetRequestStream ().Close ();
         request.GetResponse ();

         request = (HttpWebRequest)WebRequest.Create ("http://127.0.0.1:32400/:/prefs?GenerateBIFBehavior=never");
         request.Method = "PUT";
         request.GetRequestStream ().Close ();
         request.GetResponse ();
      }

      private static List<Section> GetPlexSections () {
         using (XmlTextReader reader = new XmlTextReader (ReadXmlFromURL (SectionUrl))) {
            List<Section> sections = new List<Section>();
            Section section = null;
            while (reader.Read ()) {
               switch (reader.NodeType) {
                  case XmlNodeType.Element:
                     switch (reader.Name) {
                        case "Directory":
                           section = new Section ();
                           while (reader.MoveToNextAttribute ()) {
                              if (reader.Name == "title") {
                                 section.Name = reader.Value;
                              } else if (reader.Name == "key") {
                                 section.id = reader.Value;
                              } else if (reader.Name == "type") {
                                 switch (reader.Value) {
                                    case "movie":
                                       section.Type = Category.Movie;
                                       break;
                                    case "show":
                                       section.Type = Category.TV;
                                       break;
                                 }
                              }

                           }
                           break;
                     }
                     break;
                  case XmlNodeType.EndElement:
                     switch (reader.Name) {
                        case "Directory":
                           sections.Add (section);
                           break;
                     }
                     break;
               }
            }

            return sections;
         }
      }

      private static List<MediaEntry> GetTVShowItems (string url) {

         List<MediaEntry> items = new List<MediaEntry>();
         long max = -1;
         long count = 0;
         uint percent = 0;
         uint nextPercent = 10;
         List<string> mediaUrls = new List<string>();
         List<string> leafUrls = new List<string>();

         using (XmlTextReader reader = new XmlTextReader (ReadXmlFromURL (url))) {
            while (reader.Read ()) {
               switch (reader.NodeType) {
                  case XmlNodeType.Element:
                     switch (reader.Name) {
                        case "Directory":
                           while (reader.MoveToNextAttribute ()) {
                              if (reader.Name == "key") {
                                 mediaUrls.Add (String.Format ("http://127.0.0.1:32400{0}", reader.Value));
                                 break;
                              }
                           }
                           break;
                     }
                     break;
               }
            }
         }

         foreach (string mediaUrl in mediaUrls) {
            using (XmlTextReader reader = new XmlTextReader (ReadXmlFromURL (mediaUrl))) {
               string currentKey = null;
               bool foundAllEpisodes = false;
               while (reader.Read ()) {
                  switch (reader.NodeType) {
                     case XmlNodeType.Element:
                        switch (reader.Name) {
                           case "Directory":
                              while (reader.MoveToNextAttribute ()) {
                                 if (reader.Name == "key") {
                                    currentKey = reader.Value;
                                 } else if (reader.Name == "title" && reader.Value == "All episodes") {
                                    leafUrls.Add (String.Format ("http://127.0.0.1:32400{0}", currentKey));
                                    foundAllEpisodes = true;
                                    break;
                                 } else if (reader.Name == "title" && (reader.Value.StartsWith ("Season") || reader.Value.StartsWith ("Specials"))) {
                                    if (!foundAllEpisodes) {
                                       leafUrls.Add (String.Format ("http://127.0.0.1:32400{0}", currentKey));
                                    }
                                 }
                              }
                              break;
                        }
                        break;
                  }

                  if (foundAllEpisodes) {
                     break;
                  }
               }
            }
         }

         foreach (string leafUrl in leafUrls) {
            using (XmlTextReader reader = new XmlTextReader (ReadXmlFromURL (leafUrl))) {
               long startMax = max;

               while (reader.Read ()) {
                  switch (reader.NodeType) {
                     case XmlNodeType.Element:
                        switch (reader.Name) {
                           case "MediaContainer":
                              while (reader.MoveToNextAttribute ()) {
                                 if (reader.Name == "size") {
                                    if (max == -1) {
                                       max = long.Parse (reader.Value);
                                    } else {
                                       max += long.Parse (reader.Value);
                                    }
                                    break;
                                 }
                              }
                              break;
                        }
                        break;
                  }

                  if (startMax < max) {
                     break;
                  }
               }
            }
         }

         foreach (string leafUrl in leafUrls) {
            List<MediaEntry> entries = GetMovieItems(leafUrl, ref max, ref count, ref percent, ref nextPercent);

            items.AddRange (entries);
         }

         return items;
      }

      private static List<MediaEntry> GetMovieItems (string url, ref long max, ref long count, ref uint percent, ref uint nextPercent) {
         using (XmlTextReader reader = new XmlTextReader (ReadXmlFromURL (url))) {
            List<MediaEntry> items = new List<MediaEntry>();
            MediaEntry entry = null;
            while (reader.Read ()) {
               switch (reader.NodeType) {
                  case XmlNodeType.Element:
                     switch (reader.Name) {
                        case "MediaContainer":
                           while (reader.MoveToNextAttribute ()) {
                              if (reader.Name == "size") {
                                 if (max < 0) {
                                    max = long.Parse (reader.Value);
                                 }
                              }
                           }
                           break;
                        case "Video":
                           count++;
                           Console.Write ("\rProcessing {0:P2}...", (double)count / (double)max);
                           entry = new MediaEntry () {
                              IndexProcessed = false
                           };

                           while (reader.MoveToNextAttribute ()) {
                              if (reader.Name == "key") {
                                 entry.key = reader.Value;
                                 entry.Hash = GetMediaHash (entry.Url);
                              }
                              if (reader.Name == "title") {
                                 entry.Title = reader.Value;
                              }
                           }
                           break;
                        case "Part":
                           while (reader.MoveToNextAttribute ()) {
                              if (reader.Name == "file") {
                                 entry.FilePath = reader.Value;
                              }
                              if (reader.Name == "indexes") {
                                 entry.IndexProcessed = true;
                              }
                           }
                           break;
                     }
                     break;
                  case XmlNodeType.EndElement:
                     switch (reader.Name) {
                        case "Video":
                           items.Add (entry);
                           break;
                     }
                     break;
               }
            }

            return items;
         }
      }

      private static string GetMediaHash (string url) {
         using (XmlTextReader reader = new XmlTextReader (ReadXmlFromURL (url))) {
            while (reader.Read ()) {
               switch (reader.NodeType) {
                  case XmlNodeType.Element:
                     switch (reader.Name) {
                        case "MediaPart":
                           while (reader.MoveToNextAttribute ()) {
                              if (reader.Name == "hash") {
                                 return reader.Value;
                              }
                           }
                           break;
                     }
                     break;
               }
            }

            return null;
         }
      }
   }



   class Section {
      public string Name { get; set; }
      public string id { get; set; }
      public Category Type { get; set; }
      public int Missing { get; set; }
      public int NeedsProcessing { get; set; }

      private const string baseUrl = "http://127.0.0.1:32400";

      public string Url {
         get { return String.Format ("{0}/library/sections/{1}/all", baseUrl, id); }
      }

      public override string ToString () {
         return String.Format ("Name: {0}, id: {1}", Name, id);
      }
   }

   class MediaEntry {
      public string Title { get; set; }
      private string _filePath = null;
      public string FilePath {
         get { return _filePath; }
         set {
            Uri uri = new Uri(value);
            _filePath = uri.LocalPath;
         }
      }
      public string key { get; set; }
      public string Hash { get; set; }
      public bool IndexProcessed { get; set; }

      private const string baseUrl = "http://127.0.0.1:32400";

      public void Analyze () {
         UnixSymbolicLinkInfo info = new UnixSymbolicLinkInfo(BifDatabasePath);

         if (info.Exists) {
            if (!info.IsSymbolicLink && File.Exists (BifLocalPath)) {
               info.Delete ();

               UnixFileInfo newInfo = new UnixFileInfo(BifLocalPath);
               newInfo.CreateSymbolicLink (BifDatabasePath);
            } else if (!info.IsSymbolicLink) {
               if (File.Exists (BifLocalPath)) {
                  File.Delete (BifLocalPath);
               }

               File.Move (BifDatabasePath, BifLocalPath);

               UnixFileInfo newInfo = new UnixFileInfo(BifLocalPath);
               newInfo.CreateSymbolicLink (BifDatabasePath);
            }
         } else {
            if (File.Exists (BifLocalPath)) {
               UnixFileInfo newInfo = new UnixFileInfo(BifLocalPath);
               if (!Directory.Exists (IndexDirectory)) {
                  Directory.CreateDirectory (IndexDirectory);
               }
               newInfo.CreateSymbolicLink (BifDatabasePath);
            }
         }
      }

      /// <summary>
      /// Gets a value indicating that the plex database index entry is missing but the physical index exists.
      /// Possible reasons plex hasn't processed the video file.
      /// </summary>
      /// <value><c>true</c> if missing possible entry; otherwise, <c>false</c>.</value>
      public bool MissingPossibleEntry {
         get {
            UnixSymbolicLinkInfo info = new UnixSymbolicLinkInfo(BifDatabasePath);

            if (info.Exists) {
               return !info.IsSymbolicLink;
            }

            return !info.Exists;
         }
      }

      /// <summary>
      /// Gets a value indicating that the plex database index entry is missing and the physical index is missing.
      /// </summary>
      /// <value><c>true</c> if missing entry; otherwise, <c>false</c>.</value>
      public bool MissingEntry {
         get {
            return !File.Exists (BifLocalPath);
         }
      }


      public string Url {
         get { return String.Format ("{0}{1}/tree", baseUrl, key); }
      }

      public string BifDatabasePath {
         get {
            return Path.Combine (IndexDirectory, "index-sd.bif");
         }
      }

      public string IndexDirectory {
         get {
            string part1 = Hash.Substring(0, 1);
            string part2 = Hash.Substring(1) + ".bundle";
            return Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.Personal), "Library", "Application Support", "Plex Media Server", "Media", "localhost", part1, part2, "Contents", "Indexes");
         }
      }

      public string BifLocalPath {
         get { return FilePath.Substring (0, FilePath.Length - 3) + "bif"; }
      }

      public override string ToString () {
         return String.Format ("Title: {0}, Key: {1}", Title, key);
      }

   }
}
