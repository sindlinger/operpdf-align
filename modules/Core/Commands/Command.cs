using System;
using System.Collections.Generic;

namespace Obj.Commands
{
    public abstract class Command
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract void Execute(string[] args);
        public abstract void ShowHelp();

        protected bool ParseCommonOptions(string[] args, out string inputFile, out Dictionary<string, string> options)
        {
            inputFile = "";
            options = new Dictionary<string, string>();

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("-"))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        options[args[i]] = args[i + 1];
                        i++;
                    }
                    else
                    {
                        options[args[i]] = "true";
                    }
                }
                else if (inputFile == null)
                {
                    inputFile = args[i];
                }
            }
            return true;
        }
    }
}
