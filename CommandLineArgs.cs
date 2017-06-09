using System.IO;
using Ookii.CommandLine;
using log4net;

namespace Raymond.Boten
{
    class CommandLineArgs
    {
        [CommandLineArgument(Position = 0, IsRequired = true)]
        public string UserName { get; set; }

        [CommandLineArgument(Position = 1, IsRequired = true)]
        public string Password { get; set; }

        [CommandLineArgument(Position = 2, IsRequired = true)]
        public string TeamName { get; set; }

        //optional switches
        /// <summary> set flag to prevent raymond from adding the team as reviewers of every review </summary>
        [CommandLineArgument]
        public bool NoReviewers { get; set; }

        /// <summary> set flag to prevent raymond from sending a mail to the team when a review gets too old </summary>
        [CommandLineArgument]
        public bool No24hMail { get; set; }

        /// <summary> comma separated list of projects that should not be considered </summary>
        [CommandLineArgument]
        public string ProjectBlacklist { get; set; }

        private static readonly ILog _logger = LogManager.GetLogger("Raymond.Boten");
        public static CommandLineArgs Parse(string[] args)
        {
            var parser = new CommandLineParser(typeof(CommandLineArgs));
            try
            {
                return (CommandLineArgs)parser.Parse(args);
            }
            catch (CommandLineArgumentException ex)
            {
                using (var sw = new StringWriter())
                {
                    parser.WriteUsage(sw, 80);
                    _logger.Fatal(sw.ToString());
                }
                _logger.Fatal("  <username> is f.lastname");
                _logger.Fatal("  <password> is your gerrit http password, not criteo password");
                _logger.Fatal("    you can find it in Settings > HTTP Password");
                _logger.Fatal("  <team name> must be a reviewer group in gerrit");
                _logger.Fatal("    all available groups are listed in People > List Groups");
                _logger.Fatal(ex);
            }

            return null;
        }
    }
}