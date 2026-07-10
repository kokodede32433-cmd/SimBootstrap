using System;
using System.Threading.Tasks;

namespace SimBootstrap.CLI;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("SimBootstrap CLI started.");
        await Task.CompletedTask;
    }
}
