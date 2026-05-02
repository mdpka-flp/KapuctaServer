using System.Collections.Generic;

namespace Kapuctagram.Server.Models
{
    public class ChatInfo
    {
        public long Id { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Password { get; set; }
        public List<long> AdminIds { get; set; } = new();
        public List<long> BannedIds { get; set; } = new();
        public List<long> ParticipantIds { get; set; } = new();
        public int MaxMembers { get; set; }
        public long OwnerId { get; set; }
    }
}