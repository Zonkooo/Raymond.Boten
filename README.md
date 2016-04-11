# Raymond.Boten
Watches gerrit and nudges reviewers after 23h

Use the sample config file provided to create your own.

Run with your login, your gerrit http password and a gerrit group name like:

    Raymond.Boten.exe r.vandon AbCdEf123XyZ backend-team

All repositories where the given group is owner will be considered.

It also adds members of the team as reviewers to commits. You can disable that with the `-NoReviewers` command line option.

You can create a planned task to run it every 5 minutes.
