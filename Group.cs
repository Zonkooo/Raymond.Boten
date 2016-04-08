using System.Collections.Generic;

namespace Raymond.Boten
{
    class Group
    {
        public readonly string Name;
        public readonly string Id;
        private readonly List<string> _membersMail = new List<string>();
        public IEnumerable<string> MembersMail { get { return _membersMail; } }

        public Group(string name, Helper helper)
        {
            Name = name;
            //get group id
            var groups = helper.CallGerrit("groups/");
            Id = (string)groups[name]["id"];

            //get mails of people in team
            var groupDetails = helper.CallGerrit("groups/" + Id + "/detail");
            foreach (var m in groupDetails["members"].Children())
            {
                _membersMail.Add((string)m["email"]);
            }
        }
    }
}