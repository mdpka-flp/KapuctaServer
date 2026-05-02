using System;
using System.Threading.Tasks;
using Kapuctagram.Server;

class Program
{
    static async Task Main(string[] args)
    {
        var server = new ChatServer();
        await server.StartAsync();
    }
}