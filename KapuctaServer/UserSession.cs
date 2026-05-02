using System.Collections.Generic;
using System.Net.Sockets;

namespace Kapuctagram.Server
{
    public class UserSession
    {
        public TcpClient Client { get; set; }
        public long UserId { get; set; }
        public string Name { get; set; }
        public HashSet<long> SubscribedChats { get; set; } = new();
    }
}