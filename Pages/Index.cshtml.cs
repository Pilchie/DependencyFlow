using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DependencyFlow.Pages
{
    public class IndexModel : PageModel
    {
        private static readonly Regex _repoReferenceParser = new Regex(@"^(?<owner>[A-Za-z0-9-_\.]+)/(?<repo>[A-Za-z0-9-_\.]+)");

        private readonly swaggerClient _client;

        public IndexModel(swaggerClient client)
        {
            _client = client;
        }

        public IReadOnlyList<(string Name, int Id)>? Channels { get; private set; }

        public async Task OnGet()
        {
            Channels = (await _client.ListChannelsAsync(classification: null, ApiVersion11._20190116))
                .Select(c => (c.Name, c.Id))
                .OrderBy(x => x.Name)
                .ToList();
        }

        public IActionResult OnPost(int channelId, string repo)
        {
            var match = _repoReferenceParser.Match(repo);
            var owner = "";
            if(match.Success)
            {
                owner = match.Groups["owner"].Value;
                repo = match.Groups["repo"].Value;
            }
            var page = Url.Page("Incoming", new { channelId, owner, repo });
            return page is null
                ? NotFound()
                : Redirect(page);
        }
    }
}
