using System;

namespace Obj.Commands
{
    /// <summary>
    /// Placeholder loader kept for compatibility; OBJ pipeline does not depend on the parent loader.
    /// </summary>
    public sealed class FpdfLoadCommand : Command
    {
        public override string Name => "load";
        public override string Description => "Loader placeholder (OBJ)";

        public override void Execute(string[] args)
        {
            Console.WriteLine("[load] Loader desabilitado no OBJ. Use um pipeline externo para alimentar o Postgres.");
        }

        public override void ShowHelp()
        {
            Console.WriteLine("load: desabilitado no OBJ.");
        }
    }
}
