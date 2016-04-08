using System;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Raymond.Boten
{
    class Helper
    {
        private const string GerritUrl = "http://review.criteois.lan/a/";

        private readonly WebClient _client;

        public Helper(string user, string passwd)
        {
            _client = new WebClient
                {
                    UseDefaultCredentials = true, 
                    Credentials = new NetworkCredential(user, passwd)
                };
        }

        /// <summary>
        /// sends a GET request to gerrit REST API
        /// </summary>
        public JToken CallGerrit(string query)
        {
            string response = _client.DownloadString(GerritUrl + query);
            return GerritResponseToJson(response);
        }

        /// <summary>
        /// sends a POST request to gerrit REST API with the given parameters
        /// </summary>
        public JToken CallGerrit(string query, NameValueCollection parameters)
        {
            var uri = new Uri(GerritUrl + query);
#if DEBUG
            //skip write action in gerrit
            return null;
#endif
            var response =  Encoding.UTF8.GetString(_client.UploadValues(uri, parameters));
            return GerritResponseToJson(response);
        }

        private static JToken GerritResponseToJson(string response)
        {
            response = response.Substring(response.IndexOf('\n')); //first line is just gibberish
            return (JToken)JsonConvert.DeserializeObject(response);
        }
    }
}