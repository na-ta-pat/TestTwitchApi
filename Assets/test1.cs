// using System.Collections.Generic;
// using System.IO;
// using System.Linq;
// using System.Net.Sockets;
// using System.Text.RegularExpressions;
// using System.Threading.Tasks;
// using UnityEngine;

// // Everything Twitch chat (IRC) offers, read-only, no token needed. Add to a GameObject, set Channel, press Play.
// public class TwitchChat : MonoBehaviour
// {
//     public bool on = true;
//     public string channel = "";

//     // command: PRIVMSG = chat message, USERNOTICE = sub/gift/raid/..., CLEARCHAT = ban/timeout/chat cleared,
//     // CLEARMSG = message deleted, ROOMSTATE = chat settings, JOIN/PART = someone entered/left, NOTICE, numbers = server info
//     public System.Action<string, string, string, Dictionary<string, string>> OnEvent = (command, user, text, tags) =>
//         Debug.Log($"{command} {user}: {text}\n{string.Join("  ", tags.Select(t => t.Key + "=" + t.Value))}");

//     async void Start()
//     {
//         while (this && on) // reconnects if Twitch drops the connection or asks us to (RECONNECT)
//         {
//             try
//             {
//                 using var tcp = new TcpClient();
//                 await tcp.ConnectAsync("irc.chat.twitch.tv", 6667);
//                 var reader = new StreamReader(tcp.GetStream());
//                 var writer = new StreamWriter(tcp.GetStream()) { AutoFlush = true };
//                 writer.Write($"CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership\r\nNICK justinfan12345\r\nJOIN #{channel.ToLowerInvariant()}\r\n");
//                 while (await reader.ReadLineAsync() is string line && this)
//                 {
//                     var m = Regex.Match(line, @"^(?:@(\S+) )?(?::(?:(\w+)!)?\S+ )?(\S+)(?: [^:]\S*)*(?: :(.*))?"); // [@tags] [:user!...] COMMAND [params] [:text]
//                     var tags = m.Groups[1].Value.Split(';').Where(t => t != "").ToDictionary(t => t.Split('=')[0], t => Unescape(t.Substring(t.IndexOf('=') + 1)));
//                     var command = m.Groups[3].Value;
//                     if (command == "PING") writer.Write("PONG :" + m.Groups[4].Value + "\r\n");
//                     else if (command == "RECONNECT") break;
//                     else OnEvent?.Invoke(command, m.Groups[2].Success ? m.Groups[2].Value : tags.GetValueOrDefault("login"), m.Groups[4].Value, tags);
//                 }
//             }
//             catch (System.Exception e) { if (this) Debug.LogWarning("Twitch chat: " + e); }
//             await Task.Delay(5000);
//         }
//     }

//     // Tag values escape some characters: \s = space, \: = ;, \\ = \
//     static string Unescape(string v) => Regex.Replace(v, @"\\(.)", e => e.Groups[1].Value switch { "s" => " ", ":" => ";", "r" => "\r", "n" => "\n", var c => c });
// }Vector3(1,1,1)
