using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Kapuctagram.Server.Models;
using Kapuctagram.Sdk.Protocol;

namespace Kapuctagram.Server
{
    public class ChatServer
    {
        private readonly int _port;
        private readonly string _usersFile;
        private readonly ChatManager _chatManager;
        private readonly ConcurrentDictionary<long, UserSession> _sessions = new();
        private TcpListener _listener;
        private bool _running;

        public ChatServer(int port = 1337, string usersFile = "users.txt")
        {
            _port = port;
            _usersFile = usersFile;
            _chatManager = new ChatManager("data", _usersFile);
        }

        public async Task StartAsync()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _running = true;
            Console.WriteLine($"Сервер запущен на порту {_port}");

            while (_running)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var stream = client.GetStream();
            UserSession session = null;
            try
            {
                var authMsg = await MessageParser.ReadMessageAsync(stream);
                if (authMsg.Type != 'A')
                {
                    client.Close();
                    return;
                }
                string authData = authMsg.Data;
                string[] parts = authData.Split(new[] { " | " }, StringSplitOptions.None);
                if (parts.Length != 2)
                {
                    client.Close();
                    return;
                }
                string password = parts[0];
                string requestedName = parts[1];

                long userId;
                string finalName;
                lock (this)
                {
                    var users = File.ReadAllLines(_usersFile);
                    var existing = users.FirstOrDefault(u => u.StartsWith(password + " | "));
                    if (existing != null)
                    {
                        var userParts = existing.Split(new[] { " | " }, StringSplitOptions.None);
                        userId = long.Parse(userParts[1]);
                        finalName = userParts[2];
                        Console.WriteLine($"Вход: {finalName} (ID: {userId})");
                    }
                    else
                    {
                        userId = GetNextUserId();
                        finalName = requestedName;
                        string newRecord = $"{password} | {userId} | {finalName}";
                        File.AppendAllLines(_usersFile, new[] { newRecord });
                        Console.WriteLine($"Регистрация: {finalName} (ID: {userId})");
                    }
                }

                string response = $"{userId} | {finalName}";
                await SendMessageAsync(client, 'A', response);

                session = new UserSession { Client = client, UserId = userId, Name = finalName };
                _sessions[userId] = session;

                var chatIds = _chatManager.GetUserChats(userId);
                await SendMessageAsync(client, 'S', string.Join(",", chatIds));

                while (client.Connected)
                {
                    var msg = await MessageParser.ReadMessageAsync(stream);
                    await ProcessCommandAsync(session, msg);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Клиент {session?.Name} отключился: {ex.Message}");
            }
            finally
            {
                if (session != null)
                    _sessions.TryRemove(session.UserId, out _);
                client.Close();
            }
        }

        private async Task ProcessCommandAsync(UserSession session, (char Type, string Data) msg)
        {
            switch (msg.Type)
            {
                case 'C': // Create chat
                {
                    var cParts = msg.Data.Split('|');
                    if (cParts.Length >= 3)
                    {
                        string type = cParts[0];
                        string name = cParts[1];
                        string password = cParts[2];
                        if (type == "P" || type == "G" || type == "C")
                        {
                            ChatInfo chat = null;
                            if (type == "P" && cParts.Length >= 4 && long.TryParse(cParts[3], out long targetUserId))
                            {
                                chat = _chatManager.CreatePersonalChat(type, name, password, session.UserId, targetUserId);
                            }
                            else if (type == "P")
                            {
                                await SendMessageAsync(session.Client, 'C', "ERROR|Missing target user ID");
                                break;
                            }
                            else
                            {
                                chat = _chatManager.CreateChat(type, name, password, session.UserId);
                            }
                            if (chat != null)
                            {
                                session.SubscribedChats.Add(chat.Id);
                                await SendMessageAsync(session.Client, 'C', chat.Id.ToString());
                            }
                        }
                        else await SendMessageAsync(session.Client, 'C', "ERROR|Invalid type");
                    }
                    break;
                }

                case 'J': // Join chat
                {
                    if (long.TryParse(msg.Data, out long joinChatId))
                    {
                        if (_chatManager.TryGetChat(joinChatId, out var chat))
                        {
                            if (chat.BannedIds.Contains(session.UserId))
                                await SendMessageAsync(session.Client, 'J', "ERROR|You are banned");
                            else if (chat.Type == "C" && !chat.AdminIds.Contains(session.UserId) && !chat.ParticipantIds.Contains(session.UserId))
                                await SendMessageAsync(session.Client, 'J', "ERROR|Channel read requires admin approval");
                            else
                            {
                                if (!chat.ParticipantIds.Contains(session.UserId))
                                    _chatManager.AddParticipant(joinChatId, session.UserId);
                                session.SubscribedChats.Add(joinChatId);
                                await SendMessageAsync(session.Client, 'J', "OK");
                            }
                        }
                        else await SendMessageAsync(session.Client, 'J', "ERROR|Chat not found");
                    }
                    break;
                }

                case 'L': // Leave chat
                {
                    if (long.TryParse(msg.Data, out long leaveChatId))
                    {
                        session.SubscribedChats.Remove(leaveChatId);
                        _chatManager.RemoveParticipant(leaveChatId, session.UserId);
                        await SendMessageAsync(session.Client, 'L', "OK");
                    }
                    break;
                }

                case 'M': // Send text message
                {
                    var mParts = msg.Data.Split('|');
                    if (mParts.Length >= 2 && long.TryParse(mParts[0], out long targetChatId))
                    {
                        string text = mParts[1];
                        if (_chatManager.TryGetChat(targetChatId, out var chat))
                        {
                            if (!chat.ParticipantIds.Contains(session.UserId)) return;
                            if (chat.BannedIds.Contains(session.UserId)) return;
                            if (chat.Type == "C" && !chat.AdminIds.Contains(session.UserId)) return;

                            _chatManager.SaveMessage(targetChatId, $"[{DateTime.Now:HH:mm}] {session.Name}: {text}");

                            // Рассылка участникам, у которых чат подписан
                            foreach (var subscriberId in chat.ParticipantIds)
                            {
                                if (_sessions.TryGetValue(subscriberId, out var sub) && sub.SubscribedChats.Contains(targetChatId))
                                {
                                    await SendMessageAsync(sub.Client, 'M', $"{targetChatId}|{session.Name}|{text}");
                                }
                            }

                            // Уведомление получателя в личном чате, если он ещё не подписан
                            if (chat.Type == "P")
                            {
                                var recipientId = chat.ParticipantIds.FirstOrDefault(id => id != session.UserId);
                                if (recipientId != 0 && _sessions.TryGetValue(recipientId, out var recipientSession))
                                {
                                    if (!recipientSession.SubscribedChats.Contains(targetChatId))
                                    {
                                        await SendMessageAsync(recipientSession.Client, 'N', chat.Id.ToString());
                                    }
                                }
                            }
                        }
                    }
                    break;
                }

                case 'F': // Send file
                {
                    var fParts = msg.Data.Split('|');
                    if (fParts.Length >= 3 && long.TryParse(fParts[0], out long fileChatId) && long.TryParse(fParts[2], out long fileSize))
                    {
                        string fileName = fParts[1];
                        int fileId = await SaveFileAsync(session.Client.GetStream(), fileName, fileSize, fileChatId);
                        string fileRecord = $"[FILE] {fileChatId}|{fileId}|{fileName}|{session.Name}";
                        _chatManager.SaveMessage(fileChatId, fileRecord);
                        if (_chatManager.TryGetChat(fileChatId, out var chat))
                        {
                            foreach (var subscriberId in chat.ParticipantIds)
                            {
                                if (_sessions.TryGetValue(subscriberId, out var sub) && sub.SubscribedChats.Contains(fileChatId))
                                {
                                    await SendMessageAsync(sub.Client, 'F', $"{fileChatId}|{session.Name}|{fileName}|{fileId}");
                                }
                            }
                        }
                    }
                    break;
                }

                case 'K': // Ban/Admin actions
                {
                    var kParts = msg.Data.Split('|');
                    if (kParts.Length >= 3 && long.TryParse(kParts[0], out long kickChatId) && long.TryParse(kParts[1], out long targetUserId))
                    {
                        string action = kParts[2];
                        if (_chatManager.TryGetChat(kickChatId, out var kchat) && kchat.AdminIds.Contains(session.UserId))
                        {
                            switch (action)
                            {
                                case "ban": _chatManager.BanUser(kickChatId, targetUserId); break;
                                case "unban": _chatManager.UnbanUser(kickChatId, targetUserId); break;
                                case "makeAdmin": _chatManager.AddAdmin(kickChatId, targetUserId); break;
                                case "removeAdmin": _chatManager.RemoveAdmin(kickChatId, targetUserId); break;
                            }
                            await SendMessageAsync(session.Client, 'K', "OK");
                        }
                        else await SendMessageAsync(session.Client, 'K', "ERROR|Not admin");
                    }
                    break;
                }

                case 'I': // Get chat info
                {
                    if (long.TryParse(msg.Data, out long infoChatId) && _chatManager.TryGetChat(infoChatId, out var infoChat))
                    {
                        string chatName = infoChat.Name;
                        if (infoChat.Type == "P" && session != null)
                        {
                            var otherId = infoChat.ParticipantIds.FirstOrDefault(id => id != session.UserId);
                            if (otherId != 0)
                            {
                                var otherName = _chatManager.GetUserName(otherId);
                                chatName = $"Личный чат с {otherName}";
                            }
                            else
                            {
                                chatName = "⭐ Избранное";
                            }
                        }
                        string infoData = $"{infoChat.Id}|{chatName}|{infoChat.Type}|{string.Join(",", infoChat.AdminIds)}|{string.Join(",", infoChat.BannedIds)}|{string.Join(",", infoChat.ParticipantIds)}|{infoChat.OwnerId}|{infoChat.MaxMembers}|{infoChat.Password}";
                        await SendMessageAsync(session.Client, 'I', infoData);
                    }
                    break;
                }

                case 'S': // Get user's chats
                {
                    var userChats = _chatManager.GetUserChats(session.UserId);
                    await SendMessageAsync(session.Client, 'S', string.Join(",", userChats));
                    break;
                }

                case 'U': // Update chat settings
                {
                    var uParts = msg.Data.Split('|');
                    if (uParts.Length >= 3 && long.TryParse(uParts[0], out long updateChatId))
                    {
                        string newName = uParts[1];
                        string newPassword = uParts[2];
                        if (_chatManager.TryGetChat(updateChatId, out var uchat) && uchat.AdminIds.Contains(session.UserId))
                        {
                            _chatManager.UpdateChatSettings(updateChatId, newName, newPassword);
                            await SendMessageAsync(session.Client, 'U', "OK");
                        }
                        else await SendMessageAsync(session.Client, 'U', "ERROR|Not admin");
                    }
                    break;
                }

                case 'Q': // Search
                {
                    string query = msg.Data.Trim();
                    var results = new List<string>();
                    if (long.TryParse(query, out long qId))
                    {
                        if (qId > 0)
                        {
                            var user = _chatManager.FindUserById(qId);
                            if (user.HasValue)
                                results.Add($"{user.Value.Id}|user|{user.Value.Name}|👤: {user.Value.Name} (ID: {user.Value.Id})");
                        }
                        else
                        {
                            if (_chatManager.TryGetChat(qId, out var chat))
                            {
                                string icon = chat.Type == "G" ? "💬" : (chat.Type == "C" ? "📢" : "💌");
                                results.Add($"{chat.Id}|chat|{chat.Name}|{icon} {chat.Name} (ID: {chat.Id})");
                            }
                        }
                    }
                    else
                    {
                        foreach (var u in _chatManager.FindUsersByName(query))
                            results.Add($"{u.Id}|user|{u.Name}|👤: {u.Name} (ID: {u.Id})");
                        foreach (var c in _chatManager.SearchChatsByName(query))
                        {
                            string icon = c.Type == "G" ? "💬" : (c.Type == "C" ? "📢" : "💌");
                            results.Add($"{c.Id}|chat|{c.Name}|{icon} {c.Name} (ID: {c.Id})");
                        }
                    }
                    await SendMessageAsync(session.Client, 'Q', string.Join("\n", results));
                    break;
                }
                
                case 'D': // Download file
                {
                    var dParts = msg.Data.Split('|');
                    if (dParts.Length >= 2 && long.TryParse(dParts[0], out long dChatId) && long.TryParse(dParts[1], out long dFileId))
                    {
                        string chatDir = Path.Combine("data", "chats", dChatId.ToString(), "files");
                        if (Directory.Exists(chatDir))
                        {
                            var matchingFile = Directory.GetFiles(chatDir, $"{dFileId}_*").FirstOrDefault();
                            if (matchingFile != null)
                            {
                                var fileInfo = new FileInfo(matchingFile);
                                await SendMessageAsync(session.Client, 'D', $"OK|{fileInfo.Length}");
                                using var fs = File.OpenRead(matchingFile);
                                await fs.CopyToAsync(session.Client.GetStream());
                                await session.Client.GetStream().FlushAsync();
                                // *** Добавить маркер конца передачи файла ***
                                await SendMessageAsync(session.Client, 'E', "");  // пустое сообщение
                            }
                            else
                            {
                                await SendMessageAsync(session.Client, 'D', "ERROR|File not found");
                            }
                        }
                        else
                        {
                            await SendMessageAsync(session.Client, 'D', "ERROR|Chat directory missing");
                        }
                    }
                    break;
                }
                
                case 'H': // Get chat history
                {
                    var hParts = msg.Data.Split('|');
                    if (hParts.Length >= 2 && long.TryParse(hParts[0], out long historyChatId))
                    {
                        int count = 50;
                        if (hParts.Length >= 3 && int.TryParse(hParts[2], out int requestedCount))
                            count = Math.Min(requestedCount, 200);
                        string history = await LoadHistoryAsync(historyChatId, count);
                        await SendMessageAsync(session.Client, 'H', $"{historyChatId}|{history}");
                    }
                    break;
                }
            }
        }
        
        private async Task<string> LoadHistoryAsync(long chatId, int count)
        {
            string messagesPath = Path.Combine("data", "chats", chatId.ToString(), "messages.txt");
            if (!File.Exists(messagesPath)) return "";
            var lines = await File.ReadAllLinesAsync(messagesPath);
            var lastLines = lines.Reverse().Take(count).Reverse();
            return string.Join("\n", lastLines);
        }

        private async Task SendMessageAsync(TcpClient client, char type, string data)
        {
            var stream = client.GetStream();
            byte[] dataBytes = Encoding.UTF8.GetBytes(data);
            await stream.WriteAsync(new byte[] { (byte)type });
            await stream.WriteAsync(BitConverter.GetBytes(dataBytes.Length));
            await stream.WriteAsync(dataBytes);
            await stream.FlushAsync();
        }

        private async Task<int> SaveFileAsync(NetworkStream stream, string fileName, long fileSize, long chatId)
        {
            string chatDir = Path.Combine("data", "chats", chatId.ToString(), "files");
            Directory.CreateDirectory(chatDir);
            int fileId = Directory.GetFiles(chatDir).Length + 1;
            string filePath = Path.Combine(chatDir, $"{fileId}_{fileName}");
            using var fs = File.Create(filePath);
            byte[] buffer = new byte[64 * 1024];
            long totalRead = 0;
            while (totalRead < fileSize)
            {
                int toRead = (int)Math.Min(buffer.Length, fileSize - totalRead);
                int read = await stream.ReadAsync(buffer, 0, toRead);
                if (read == 0) throw new EndOfStreamException();
                await fs.WriteAsync(buffer, 0, read);
                totalRead += read;
            }
            return fileId;
        }

        private long GetNextUserId()
        {
            var users = File.ReadAllLines(_usersFile);
            long max = 0;
            foreach (var line in users)
            {
                var parts = line.Split(new[] { " | " }, StringSplitOptions.None);
                if (parts.Length >= 2 && long.TryParse(parts[1], out long id))
                    max = Math.Max(max, id);
            }
            return max + 1;
        }
    }
}