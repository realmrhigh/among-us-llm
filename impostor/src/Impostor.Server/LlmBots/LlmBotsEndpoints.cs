using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Impostor.Api.Games;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Impostor.Server.LlmBots
{
    /// <summary>
    ///     Control panel and API of the LLM bots. Only the server machine may use it unless a token is configured.
    /// </summary>
    internal static class LlmBotsEndpoints
    {
        public static void MapLlmBots(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/llmbots", (HttpContext http, IOptions<LlmBotsConfig> config) =>
                Allowed(http, config.Value, null) ? Results.Content(PageHtml, "text/html", Encoding.UTF8) : Results.StatusCode(403));

            endpoints.MapGet("/llmbots/status", (HttpContext http, BotManager manager, IOptions<LlmBotsConfig> config) =>
            {
                if (!Allowed(http, config.Value, null))
                {
                    return Results.StatusCode(403);
                }

                var llm = manager.Llm;
                return Results.Json(new
                {
                    enabled = config.Value.Enabled,
                    brain = manager.BrainDescription,
                    llm = llm == null ? null : new { hasKey = llm.HasKey, available = llm.Available, calls = llm.Calls, failures = llm.Failures, lastModel = llm.LastModel, models = llm.DescribeModels(), requestsThisRun = llm.Budget.Total, check = manager.LlmCheck },
                    bots = manager.List(),
                    recent = manager.RecentLines.TakeLast(60),
                });
            });

            endpoints.MapGet("/llmbots/state", (HttpContext http, BotManager manager, IOptions<LlmBotsConfig> config) =>
            {
                if (!Allowed(http, config.Value, null))
                {
                    return Results.StatusCode(403);
                }

                return Results.Json(manager.Snapshot(http.Request.Query["spoilers"] == "1"));
            });

            endpoints.MapMethods("/llmbots/join", new[] { "GET", "POST" }, async (HttpContext http, BotManager manager, IOptions<LlmBotsConfig> config) =>
            {
                if (!Allowed(http, config.Value, null))
                {
                    return Results.StatusCode(403);
                }

                var code = http.Request.Query["code"].ToString();
                var count = int.TryParse(http.Request.Query["count"], out var n) ? n : 1;
                var result = await manager.AddAsync(code, count);
                return Results.Json(result, statusCode: result.Ok ? 200 : 400);
            });

            endpoints.MapMethods("/llmbots/leave", new[] { "GET", "POST" }, async (HttpContext http, BotManager manager, IOptions<LlmBotsConfig> config) =>
            {
                if (!Allowed(http, config.Value, null))
                {
                    return Results.StatusCode(403);
                }

                var code = http.Request.Query["code"].ToString();
                GameCode? parsed = string.IsNullOrWhiteSpace(code) ? default(GameCode?) : GameCode.From(code.Trim().ToUpperInvariant());
                var removed = await manager.RemoveAsync(parsed);
                return Results.Json(new { removed });
            });
        }

        private static bool Allowed(HttpContext http, LlmBotsConfig config, string? token)
        {
            if (!config.Enabled)
            {
                return false;
            }

            // A web page in the user's browser must not be able to steer the bots.
            var site = http.Request.Headers["Sec-Fetch-Site"].ToString();
            if (site.Equals("cross-site", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // A tunnel or reverse proxy on this machine makes remote requests look local, so they never count as local.
            var proxied = http.Request.Headers.ContainsKey("X-Forwarded-For") || http.Request.Headers.ContainsKey("CF-Connecting-IP") || http.Request.Headers.ContainsKey("Forwarded") || http.Request.Headers.ContainsKey("X-Real-IP");
            var remote = http.Connection.RemoteIpAddress;
            if (!proxied && remote != null && (IPAddress.IsLoopback(remote) || (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4()))))
            {
                return true;
            }

            token ??= http.Request.Query["token"].ToString();
            return !string.IsNullOrEmpty(config.ControlToken) && string.Equals(token, config.ControlToken, StringComparison.Ordinal);
        }

        private const string PageHtml = @"<!doctype html>
<html><head><meta charset='utf-8'><title>LLM bots</title>
<style>
body{font-family:system-ui,sans-serif;margin:2rem auto;max-width:60rem;padding:0 1rem;background:#12141a;color:#e6e8ee}
h1{margin-top:0} table{border-collapse:collapse;width:100%} td,th{padding:.3rem .6rem;border-bottom:1px solid #2a2f3a;text-align:left}
input,button{font:inherit;padding:.35rem .6rem;border-radius:6px;border:1px solid #3a4152;background:#1c2029;color:inherit}
button{cursor:pointer;background:#3b6ef5;border-color:#3b6ef5} pre{background:#0d0f14;padding:.8rem;border-radius:8px;max-height:22rem;overflow:auto;white-space:pre-wrap}
.pill{display:inline-block;padding:.1rem .5rem;border-radius:99px;background:#2a2f3a;font-size:.85em}
</style></head><body>
<h1>LLM bots</h1>
<p id='brain'>loading...</p>
<p>Add bots to a lobby: game code <input id='code' size='8' placeholder='ABCDEF'> number <input id='count' type='number' value='4' min='1' max='12' style='width:4rem'>
<button onclick='add()'>Add bots</button> <button onclick='leave()' style='background:#8a2f3a;border-color:#8a2f3a'>Remove all</button> <span id='msg'></span></p>
<p>You can also type <b>!bots 4</b> in the chat of a lobby you host.</p>
<table><thead><tr><th>Bot</th><th>Game</th><th>Role</th><th>Alive</th><th>Room</th><th>Doing</th><th>Brain</th></tr></thead><tbody id='rows'></tbody></table>
<h3>Map <label style='font-size:.8em;font-weight:normal'><input type='checkbox' id='spoil'> show roles (spoilers!)</label></h3>
<svg id='map' viewBox='0 0 100 60' style='width:100%;max-height:34rem;background:#0d0f14;border-radius:8px'></svg>
<h3>Meeting chat</h3><pre id='log'></pre>
<script>
async function refresh(){try{const r=await fetch('/llmbots/status');const s=await r.json();
document.getElementById('brain').innerHTML='Brain: <b>'+s.brain+'</b>'+(s.llm?' <span class=pill>LLM calls '+s.llm.calls+' / failed '+s.llm.failures+(s.llm.lastModel?' / '+s.llm.lastModel:'')+'</span>':'');
document.getElementById('rows').innerHTML=s.bots.map(b=>'<tr><td>'+b.name+'</td><td>'+b.game+'</td><td>'+b.role+'</td><td>'+(b.alive?'yes':'no')+'</td><td>'+b.room+'</td><td>'+b.activity+'</td><td>'+b.brain+'</td></tr>').join('');
document.getElementById('log').textContent=s.recent.join('\n');}catch(e){}}
async function add(){const r=await fetch('/llmbots/join?code='+document.getElementById('code').value+'&count='+document.getElementById('count').value,{method:'POST'});const j=await r.json();document.getElementById('msg').textContent=j.message;refresh()}
async function leave(){await fetch('/llmbots/leave',{method:'POST'});refresh()}
const COL={Red:'#c51111',Blue:'#132ed1',Green:'#117f2d',Pink:'#ed54ba',Orange:'#ef7d0d',Yellow:'#f5f557',Black:'#3f474e',White:'#d6e0f0',Purple:'#6b2fbb',Brown:'#71491e',Cyan:'#38fedc',Lime:'#50ef39',Maroon:'#5f1d2d',Rose:'#ecc0d3',Banana:'#fffebe',Gray:'#708496',Tan:'#928776',Coral:'#ec7578'};
async function drawMap(){try{const r=await fetch('/llmbots/state'+(document.getElementById('spoil').checked?'?spoilers=1':''));const s=await r.json();const svg=document.getElementById('map');
if(!s.games.length){svg.innerHTML=`<text x='50' y='30' fill='#888' font-size='3' text-anchor='middle'>no game with bots yet</text>`;return}
const g=s.games[0],m=s.maps[g.map];let xs=m.nodes.map(n=>n[0]),ys=m.nodes.map(n=>n[1]);const x0=Math.min(...xs)-3,x1=Math.max(...xs)+3,y0=Math.min(...ys)-3,y1=Math.max(...ys)+3;
const W=x1-x0,H=y1-y0;svg.setAttribute('viewBox',`0 0 ${W} ${H}`);const X=x=>x-x0,Y=y=>y1-y;let h='';
h+=m.edges.map(e=>`<line x1='${X(m.nodes[e[0]][0])}' y1='${Y(m.nodes[e[0]][1])}' x2='${X(m.nodes[e[1]][0])}' y2='${Y(m.nodes[e[1]][1])}' stroke='#2a3040' stroke-width='.3'/>`).join('');
const seen={};m.nodes.forEach(n=>{if(!seen[n[2]]&&n[2]!='Hallway'&&!/Hall$/.test(n[2])){seen[n[2]]=1;h+=`<text x='${X(n[0])}' y='${Y(n[1])-1.5}' fill='#4a5266' font-size='1.5' text-anchor='middle'>${n[2]}</text>`}});
h+=g.bodies.map(b=>`<text x='${X(b.x)}' y='${Y(b.y)}' fill='#e33' font-size='2.4' text-anchor='middle'>x</text>`).join('');
h+=g.players.map(p=>`<g opacity='${p.alive?1:.35}'><circle cx='${X(p.x)}' cy='${Y(p.y)}' r='.75' fill='${COL[p.color]||'#999'}' stroke='${p.bot?'#fff':'#ffc400'}' stroke-width='.25'/><text x='${X(p.x)}' y='${Y(p.y)-1.2}' fill='#ddd' font-size='1.3' text-anchor='middle'>${p.name}${p.role&&p.role.indexOf('Impostor')>=0?' !':''}</text></g>`).join('');
svg.innerHTML=h}catch(e){}}
setInterval(refresh,2000);setInterval(drawMap,500);refresh();drawMap();
</script></body></html>";
    }
}
