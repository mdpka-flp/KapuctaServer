using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kapuctagram.Server.Models;

namespace Kapuctagram.Server
{
    public class ChatManager
    {
        private readonly string _dataPath;
        private readonly string _usersFile;
        private readonly ConcurrentDictionary<long, ChatInfo> _chats = new();
        private long _nextChatId = -1;
        private readonly object _chatIdLock = new();

        public ChatManager(string dataPath = "data", string usersFile = "users.txt")
        {
            _dataPath = dataPath;
            _usersFile = usersFile;
            Directory.CreateDirectory(Path.Combine(_dataPath, "chats"));
            LoadAllChats();
            LoadNextChatId();
        }

        private void LoadAllChats()
        {
            string chatsDir = Path.Combine(_dataPath, "chats");
            if (!Directory.Exists(chatsDir)) return;
            foreach (var dir in Directory.GetDirectories(chatsDir))
            {
                string idStr = Path.GetFileName(dir);
                if (long.TryParse(idStr, out long chatId) && chatId < 0)
                {
                    string infoPath = Path.Combine(dir, "info.json");
                    if (File.Exists(infoPath))
                    {
                        string json = File.ReadAllText(infoPath);
                        var chat = JsonSerializer.Deserialize<ChatInfo>(json);
                        if (chat != null)
                            _chats[chatId] = chat;
                    }
                }
            }
        }

        private void LoadNextChatId()
        {
            if (_chats.Keys.Any())
                _nextChatId = _chats.Keys.Min() - 1;
            else
                _nextChatId = -1;
        }

        private void SaveChat(ChatInfo chat)
        {
            string chatDir = Path.Combine(_dataPath, "chats", chat.Id.ToString());
            Directory.CreateDirectory(chatDir);
            string infoPath = Path.Combine(chatDir, "info.json");
            string json = JsonSerializer.Serialize(chat, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(infoPath, json);
        }

        public ChatInfo CreateChat(string type, string name, string password, long ownerId)
        {
            long chatId;
            lock (_chatIdLock)
            {
                chatId = _nextChatId--;
                var nextIdFile = Path.Combine(_dataPath, "nextChatId.txt");
                File.WriteAllText(nextIdFile, _nextChatId.ToString());
            }

            var chat = new ChatInfo
            {
                Id = chatId,
                Type = type,
                Name = name,
                Password = password,
                OwnerId = ownerId,
                MaxMembers = type == "P" ? 2 : int.MaxValue
            };
            chat.ParticipantIds.Add(ownerId);
            if (type != "P")
                chat.AdminIds.Add(ownerId);
            _chats[chatId] = chat;
            SaveChat(chat);
            return chat;
        }

        public ChatInfo CreatePersonalChat(string type, string name, string password, long ownerId, long targetUserId)
        {
            var existingId = FindPersonalChat(ownerId, targetUserId);
            if (existingId.HasValue && TryGetChat(existingId.Value, out var existingChat))
                return existingChat;
            var chat = CreateChat(type, name, password, ownerId);
            AddParticipant(chat.Id, targetUserId);
            return chat;
        }
        
        public long? FindPersonalChat(long userId1, long userId2)
        {
            return _chats.Values
                .Where(c => c.Type == "P" && 
                            c.ParticipantIds.Contains(userId1) && 
                            c.ParticipantIds.Contains(userId2))
                .Select(c => (long?)c.Id)
                .FirstOrDefault();
        }

        public bool TryGetChat(long chatId, out ChatInfo chat) => _chats.TryGetValue(chatId, out chat);

        public void AddParticipant(long chatId, long userId)
        {
            if (_chats.TryGetValue(chatId, out var chat))
            {
                if (!chat.ParticipantIds.Contains(userId))
                    chat.ParticipantIds.Add(userId);
                SaveChat(chat);
            }
        }

        public void RemoveParticipant(long chatId, long userId)
        {
            if (_chats.TryGetValue(chatId, out var chat))
            {
                chat.ParticipantIds.Remove(userId);
                SaveChat(chat);
            }
        }

        public void BanUser(long chatId, long userId)
        {
            if (_chats.TryGetValue(chatId, out var chat))
            {
                if (!chat.BannedIds.Contains(userId))
                    chat.BannedIds.Add(userId);
                chat.ParticipantIds.Remove(userId);
                SaveChat(chat);
            }
        }

        public void UnbanUser(long chatId, long userId)
        {
            if (_chats.TryGetValue(chatId, out var chat))
            {
                chat.BannedIds.Remove(userId);
                SaveChat(chat);
            }
        }

        public void AddAdmin(long chatId, long userId)
        {
            if (_chats.TryGetValue(chatId, out var chat) && chat.Type != "P")
            {
                if (!chat.AdminIds.Contains(userId))
                    chat.AdminIds.Add(userId);
                SaveChat(chat);
            }
        }

        public void RemoveAdmin(long chatId, long userId)
        {
            if (_chats.TryGetValue(chatId, out var chat) && chat.Type != "P")
            {
                chat.AdminIds.Remove(userId);
                SaveChat(chat);
            }
        }

        public void UpdateChatSettings(long chatId, string newName, string newPassword)
        {
            if (_chats.TryGetValue(chatId, out var chat))
            {
                if (!string.IsNullOrEmpty(newName)) chat.Name = newName;
                if (!string.IsNullOrEmpty(newPassword)) chat.Password = newPassword;
                SaveChat(chat);
            }
        }

        public List<long> GetUserChats(long userId)
        {
            return _chats.Values.Where(c => c.ParticipantIds.Contains(userId)).Select(c => c.Id).ToList();
        }

        public void SaveMessage(long chatId, string messageLine)
        {
            string chatDir = Path.Combine(_dataPath, "chats", chatId.ToString());
            Directory.CreateDirectory(chatDir);
            string messagesPath = Path.Combine(chatDir, "messages.txt");
            File.AppendAllText(messagesPath, messageLine + Environment.NewLine);
        }

        // ---- Методы поиска для работы с запросом Q ----
        public (long Id, string Name)? FindUserById(long id)
        {
            if (!File.Exists(_usersFile)) return null;
            var lines = File.ReadAllLines(_usersFile);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { " | " }, StringSplitOptions.None);
                if (parts.Length >= 3 && long.TryParse(parts[1], out long uid) && uid == id)
                    return (uid, parts[2]);
            }
            return null;
        }

        public List<(long Id, string Name)> FindUsersByName(string nameSubstring)
        {
            var result = new List<(long, string)>();
            if (!File.Exists(_usersFile)) return result;
            var lines = File.ReadAllLines(_usersFile);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { " | " }, StringSplitOptions.None);
                if (parts.Length >= 3 && parts[2].Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((long.Parse(parts[1]), parts[2]));
                }
            }
            return result;
        }

        public List<ChatInfo> SearchChatsByName(string nameSubstring)
        {
            return _chats.Values
                .Where(c => c.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        
        public string GetUserName(long userId)
        {
            if (!File.Exists(_usersFile)) return userId.ToString();
            var lines = File.ReadAllLines(_usersFile);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { " | " }, StringSplitOptions.None);
                if (parts.Length >= 3 && long.TryParse(parts[1], out long uid) && uid == userId)
                    return parts[2];
            }
            return userId.ToString();
        }
    }
}