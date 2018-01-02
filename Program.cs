using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;
using Newtonsoft.Json.Linq;
using log4net;
using log4net.Config;

namespace Raymond.Boten
{
    //TODO
    //
    // mail about lost +2 (after 1 hour ?)

    internal static class Program
    {
        private static readonly ILog _logger = LogManager.GetLogger("Raymond.Boten");
        private static readonly DateTime _now = DateTime.UtcNow;

        private static string _smtp;
        private static string _sender;
        private static string _gerritUrl;

        [STAThread]
        private static void Main(string[] rawArgs)
        {
            XmlConfigurator.Configure(); //log4net init
            var sw = Stopwatch.StartNew();

            var args = CommandLineArgs.Parse(rawArgs);
            if (args == null) return;

            //read settings
            _smtp = ConfigurationManager.AppSettings["SmtpServer"];
            _sender = ConfigurationManager.AppSettings["SenderMail"];
            _gerritUrl = ConfigurationManager.AppSettings["GerritUrl"];

            Helper helper = new Helper(args.UserName, args.Password);

            HashSet<string> projectBlacklist = new HashSet<string>();
            if(args.ProjectBlacklist != null)
                projectBlacklist = new HashSet<string>(args.ProjectBlacklist.Split(','));

            try
            {
                var group = new Group(args.TeamName, helper);
                var changes = GetChangesFor(group, helper, projectBlacklist);

                if(!args.NoReviewers)
                    AddReviewers(changes, args.TeamName, helper);
                if(!args.No24hMail)
                    CheckFeedback(changes, group, helper);

                SaveSuccessTime();
                _logger.Info("done in " + sw.ElapsedMilliseconds + "ms");
            }
            catch (Exception e)
            {
                _logger.Error(e);
                if (GetCurrentTimeStamp() - GetLastSuccessfulRunTimeStamp() > 3*3600)
                {
                    _logger.Fatal("Raymond has not run successfully for the last 3 hours");
                    throw;
                }
            }
        }

        private static void AddReviewers(List<string> changes, string groupName, Helper helper)
        {
            //add team/user to reviewers on those changes
            foreach (var change in changes)
            {
                var encodedChangeId = change.Replace("/", "%2F");
                helper.CallGerrit("changes/" + encodedChangeId + "/reviewers", new NameValueCollection {{"reviewer", groupName}});
                //TODO when team is too big, adding reviewers needs to be validated. Read response to know if needed.
            }
        }

        private static void CheckFeedback(List<string> changes, Group group, Helper helper)
        {
            var feedbackNeeded = new Dictionary<int, JToken>();
            foreach (var change in changes)
            {
                bool receivedFeedback = false;
                var encodedChangeId = change.Replace("/", "%2F");
                var detail = helper.CallGerrit("changes/" + encodedChangeId + "/detail");
                if (detail["labels"]["Code-Review"]["all"] != null)
                {
                    foreach (var cr in detail["labels"]["Code-Review"]["all"])
                    {
                        //exclude reviews that have a +2, -2 or -1
                        if ((int) cr["value"] == 2 || (int) cr["value"] == -1 || (int) cr["value"] == -2)
                        {
                            receivedFeedback = true;
                            break;
                        }
                    }
                }
                //exclude reviews that don't have a +1 verified
                bool verified = detail["labels"]["Verified"]["approved"] != null;

                //check oldness
                var isOld = CheckOldness(detail);

                if (!receivedFeedback && verified && isOld)
                    feedbackNeeded.Add((int) detail["_number"], detail);
            }
            if (feedbackNeeded.Count > 0)
            {
                NudgeReviewers(group.MembersMail, feedbackNeeded);
            }
        }

        private static List<string> GetChangesFor(Group @group, Helper helper, HashSet<string> projectBlacklist)
        {
            //list all projects
            var projects = helper.CallGerrit("projects/?d");
            List<string> ownedProjects = new List<string>();
            foreach (JProperty project in projects)
            {
                //and save those owned by the team
                if (GetOwners(helper, project.Name).Contains(group.Id)
                    && !projectBlacklist.Contains(project.Name))
                    ownedProjects.Add(project.Name);
            }

            _logger.Info(ownedProjects.Count + " projects owned");
            Print(ownedProjects);

            //get changes on the owned projects
            var changes = new List<string>();
            foreach (var project in ownedProjects)
            {
                changes.AddRange(GetChangesOnProject(helper, project));
            }

            _logger.Info(changes.Count + " changes");
            Print(changes);
            return changes;
        }

        /// <summary>
        /// checks all comments on the change to find the latest significant feedback,
        /// typically a new patch set, or a +2 or -1 from someone.
        /// </summary>
        /// <param name="detail"></param>
        /// <returns></returns>
        private static bool CheckOldness(JToken detail)
        {
            bool isOld = false;

            var lastUpdate = DateTime.Parse(detail["created"].Value<string>());
            var comments = detail["messages"];
            var owner = detail["owner"]["username"].Value<string>();
            foreach (var comment in comments)
            {
                var message = comment["message"].Value<string>();
                //always consider new patch sets
                if (message.StartsWith("Uploaded patch set"))
                {
                    lastUpdate = Max(lastUpdate, DateTime.Parse(comment["date"].Value<string>()));
                    continue;
                }

                if (comment["author"] == null) //author is null for messages "Change could not be merged because..."
                    continue;
                //discard comments by author or qabot
                var author = comment["author"]["username"].Value<string>();
                if (author == owner || author == "qabot")
                    continue;

                //discard auto-re-adding
                if (message.EndsWith("Automatically re-added by Gerrit trivial rebase detection script."))
                    continue;

                //discard +1s
                if (message.Contains("Code-Review+1"))
                    continue;

                lastUpdate = Max(lastUpdate, DateTime.Parse(comment["date"].Value<string>()));
            }

            var oldness = _now - lastUpdate;

            //manage weekends
            if (lastUpdate.DayOfWeek == DayOfWeek.Friday 
                && (_now.DayOfWeek == DayOfWeek.Saturday
                || _now.DayOfWeek == DayOfWeek.Sunday
                || _now.DayOfWeek == DayOfWeek.Monday))
                oldness = oldness.Subtract(TimeSpan.FromDays(2.0));
            if (oldness > TimeSpan.FromHours(23))
                isOld = true;
            return isOld;
        }

        private static DateTime Max(DateTime a, DateTime b)
        {
            return a > b ? a : b;
        }

        private static void NudgeReviewers(IEnumerable<string> to, Dictionary<int, JToken> feedbackNeeded)
        {
            //load known old changes
            int[] known;
            using (var reader = new StreamReader("knownOldChanges.txt"))
            {
                known = reader.ReadToEnd().Split(new[]{'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries).Select(Int32.Parse).ToArray();
            }

            var newold = feedbackNeeded.Keys.Except(known).ToList();
            var oldold = feedbackNeeded.Keys.Intersect(known).ToList();

            if (newold.Count > 0)
            {
                MailMessage mail;
                if (newold.Count == 1)
                {
                    var change = feedbackNeeded[newold[0]];
                    var alreadyLooked = change["labels"]["Code-Review"]["all"].Where(o => (int)o["value"] == 1).Select(o => (string)o["email"]).ToList(); //people who already put a +1
                    alreadyLooked.Add((string)change["owner"]["email"]); //add owner

                    mail = new MailMessage(_sender, String.Join(",", to.Except(alreadyLooked)))
                        {
                            Subject = "Take a look at this review",
                        };

                    mail.Body = "Hello," + Environment.NewLine
                                + "The following change needs feedback :" + Environment.NewLine
                                + FormatReview(newold[0], change);
                }
                else
                {
                    mail = new MailMessage(_sender, String.Join(",", to))
                    {
                        Subject = "Take a look at these reviews",
                    };

                    mail.Body = "Hello," + Environment.NewLine
                                + "The following changes need feedback :";
                    foreach (var id in newold)
                    {
                        mail.Body += Environment.NewLine + FormatReview(id, feedbackNeeded[id]) + Environment.NewLine;
                    }
                }

                if (oldold.Count != 0)
                {
                    mail.Body += Environment.NewLine;
                    mail.Body += Environment.NewLine;
                    mail.Body += "---";
                    mail.Body += Environment.NewLine;
                    mail.Body += "Also, I hate to repeat myself, but this is still waiting for feedback :";
                    foreach (var id in oldold)
                    {
                        mail.Body += Environment.NewLine + FormatReview(id, feedbackNeeded[id]) + Environment.NewLine;
                    }
                }

                //send mail
                using (SmtpClient smtp = new SmtpClient(_smtp))
                {
#if !DEBUG //prevent unintentional mail sending when debugging
                    smtp.Send(mail);
#endif
                }
            }

            //save known old changes
            using (var writer = new StreamWriter("knownOldChanges.txt"))
            {
                writer.Write(String.Join(Environment.NewLine, feedbackNeeded.Keys));
            }
        }

        private static string FormatReview(int id, JToken change)
        {
            return String.Format("\t[{0}] {1}\r\n\t{2}{3} by {4}",
                change["project"],
                change["subject"],
                _gerritUrl,
                id,
                change["owner"]["username"]);
        }

        private static long GetLastSuccessfulRunTimeStamp()
        {
            using (var reader = new StreamReader("lastsuccess.txt"))
            {
                return Int64.Parse(reader.ReadToEnd());
            }
        }

        private static void SaveSuccessTime()
        {
            using (var writer = new StreamWriter("lastsuccess.txt"))
            {
                writer.WriteLine(GetCurrentTimeStamp());
            }
        }

        private static long GetCurrentTimeStamp()
        {
            return (long)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds);
        }

        private static void Print(IEnumerable<string> list)
        {
            foreach (var s in list)
                _logger.Debug(s);
        }

        private static IEnumerable<string> GetChangesOnProject(Helper helper, string project)
        {
            var json = helper.CallGerrit("changes/?q=status:open+-is:draft+project:" + project);
            foreach (var change in json.Children())
                yield return (string)change["id"];
        }

        ///<returns>the ID of the group owning the project</returns>
        private static IEnumerable<string> GetOwners(Helper helper, string project)
        {
            var details = helper.CallGerrit("access/?project=" + project);

            var local = details[project]["local"];
            if (((JObject)local).Count == 0)
                yield break;

            var permissions = local.First().First()["permissions"];
            if (((JObject)permissions).Count == 0)
                yield break;

            if (permissions.Children<JProperty>().Any(p => p.Name == "owner")) //some special projects don't have owner
            {
                foreach (JProperty groupId in permissions["owner"]["rules"].Children())
                {
                    yield return groupId.Name;
                }
            }
        }
    }
}