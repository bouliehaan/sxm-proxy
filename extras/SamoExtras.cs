// Additions that live in THIS repo, not upstream, and get compiled into the
// proxy at image build time. Kept as a separate file with a single injected
// call site so the sxm-player checkout stays pristine and `git pull` is clean.
//
// Adds two routes:
//   GET /channels  — the lineup as JSON, served from the in-process cache
//   GET /ui        — a channel browser, because upstream's Blazor UI is dead
//                    (_framework/blazor.web.js is missing from the published
//                    output, so its circuit never starts and clicks do nothing)
//
// Neither route costs a SiriusXM request. GetChannelsAsync() returns the cached
// _allChannels list once it is populated, so the lineup is fetched exactly once
// per process lifetime no matter how much anyone browses. That matters here:
// hammering their API is what gets an account flagged, and this design cannot.

using SXMPlayer;

public static class SamoExtras
{
    public static void MapSamoExtras(this WebApplication app, SiriusXMPlayer sxm)
    {
        // The lineup, filtered to what this subscription can actually play.
        // Unentitled channels are dropped rather than shown-and-disabled: a
        // picker that lists things which 403 on click is worse than a shorter
        // list that always works.
        app.MapGet("/channels", async () =>
        {
            var channels = await sxm.GetChannelsAsync();
            var list = channels
                // The container mixes entity types — only channel-linear is a
                // playable station. Without this the list is 712 rows instead of
                // 434, and the extras 500 on click because SetCurrentChannelAsync
                // can't resolve them.
                .Where(c => c.Entity.Type == "channel-linear")
                .Where(c => c.Decorations?.Unentitled != true)
                .Select(c => new
                {
                    id = c.Entity.Id,
                    name = c.Entity.ChannelName,
                    description = c.Entity.ChannelDescription,
                    number = c.Decorations?.ChannelNumber is double n ? (int?)n : null,
                    genre = c.Decorations?.Genre,
                })
                .Where(c => !string.IsNullOrWhiteSpace(c.id) && !string.IsNullOrWhiteSpace(c.name))
                .OrderBy(c => c.number ?? int.MaxValue)
                .ToList();
            return Results.Json(list);
        });

        app.MapGet("/ui", () => Results.Content(UiHtml, "text/html; charset=utf-8"));
    }

    // Styled to samo-server's own design language (pure black, monospace, no
    // border radius, white accent) so moving between the two doesn't feel like
    // changing tools.
    private const string UiHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>SXM Channels</title>
<style>
  :root{
    --bg:#000; --surface:#151515; --surface-2:#1f1f1f; --surface-high:#2b2b2b;
    --line:rgba(255,255,255,.08); --line-strong:rgba(255,255,255,.15);
    --text:#f2f2f2; --text-dim:#9e9e9e; --muted:#6a6a6a;
    --mono:ui-monospace,"SF Mono","JetBrains Mono",Menlo,monospace;
  }
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--text);font-family:var(--mono);font-size:13px;line-height:1.5}
  header{position:sticky;top:0;background:var(--bg);border-bottom:1px solid var(--line);padding:14px 18px;z-index:5}
  h1{margin:0 0 10px;font-size:13px;font-weight:500;letter-spacing:.14em;text-transform:uppercase;color:var(--text-dim)}
  input{width:100%;padding:9px 11px;background:var(--surface);color:var(--text);
        border:1px solid var(--line-strong);border-radius:0;font-family:var(--mono);font-size:13px;outline:none}
  input:focus{border-color:rgba(255,255,255,.42)}
  #count{margin-top:8px;color:var(--muted);font-size:11px;letter-spacing:.06em}
  main{padding:0 0 90px}
  .row{display:flex;align-items:center;gap:14px;padding:9px 18px;border-bottom:1px solid var(--line);cursor:pointer}
  .row:hover{background:var(--surface)}
  .row.sel{background:var(--surface-2);box-shadow:inset 2px 0 0 #fff}
  .num{min-width:44px;color:var(--muted);font-variant-numeric:tabular-nums}
  .nm{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  .desc{color:var(--muted);font-size:11px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:38%}
  footer{position:fixed;left:0;right:0;bottom:0;background:var(--surface);border-top:1px solid var(--line-strong);padding:11px 18px}
  .urlrow{display:flex;gap:9px;align-items:center}
  code{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;
       background:var(--bg);border:1px solid var(--line);padding:7px 9px;color:var(--text-dim);font-size:11px}
  button{background:var(--surface-high);color:var(--text);border:1px solid var(--line-strong);
         border-radius:0;padding:7px 13px;font-family:var(--mono);font-size:11px;letter-spacing:.08em;
         text-transform:uppercase;cursor:pointer;white-space:nowrap}
  button:hover{background:#3a3a3a}
  button:disabled{opacity:.35;cursor:default}
  #np{color:var(--muted);font-size:11px;margin-top:7px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  .empty{padding:30px 18px;color:var(--muted)}
</style>
</head>
<body>
<header>
  <h1>SiriusXM &rarr; samo</h1>
  <input id="q" placeholder="filter by name or channel number" autocomplete="off" autofocus>
  <div id="count">loading lineup&hellip;</div>
</header>
<main id="list"><div class="empty">loading&hellip;</div></main>
<footer>
  <div class="urlrow">
    <code id="url">select a channel</code>
    <button id="copy" disabled>copy</button>
    <button id="play" disabled>play</button>
  </div>
  <div id="np"></div>
  <audio id="audio"></audio>
</footer>
<script>
const $=s=>document.querySelector(s);
let all=[],sel=null,npTimer=null;

const urlFor=id=>location.origin+'/icecast/'+id;

function render(){
  const q=$('#q').value.trim().toLowerCase();
  const rows=all.filter(c=>!q||(c.name||'').toLowerCase().includes(q)||String(c.number||'').startsWith(q));
  $('#count').textContent=rows.length+' of '+all.length+' channels';
  if(!rows.length){$('#list').innerHTML='<div class="empty">nothing matches that</div>';return;}
  $('#list').innerHTML=rows.map(c=>
    '<div class="row'+(sel&&sel.id===c.id?' sel':'')+'" data-id="'+c.id+'">'+
      '<span class="num">'+(c.number??'')+'</span>'+
      '<span class="nm">'+esc(c.name)+'</span>'+
      '<span class="desc">'+esc(c.description||'')+'</span>'+
    '</div>').join('');
}
function esc(s){const d=document.createElement('div');d.textContent=s||'';return d.innerHTML;}

function select(id){
  sel=all.find(c=>c.id===id); if(!sel)return;
  $('#url').textContent=urlFor(sel.id);
  $('#copy').disabled=false; $('#play').disabled=false;
  render();
}

$('#list').addEventListener('click',e=>{
  const row=e.target.closest('.row'); if(row)select(row.dataset.id);
});
$('#q').addEventListener('input',render);

$('#copy').addEventListener('click',async()=>{
  try{await navigator.clipboard.writeText(urlFor(sel.id));flash('copied');}
  catch{ // clipboard needs a secure context; over plain http on a LAN IP it is blocked
    const r=document.createRange();r.selectNode($('#url'));
    getSelection().removeAllRanges();getSelection().addRange(r);flash('selected — press ctrl/cmd+c');}
});
function flash(msg){const b=$('#copy'),t=b.textContent;b.textContent=msg;setTimeout(()=>b.textContent=t,1400);}

// Preview plays through the same /icecast endpoint samo will use, so a working
// preview is real evidence the URL works rather than a separate code path.
$('#play').addEventListener('click',()=>{
  const a=$('#audio');
  if(!a.paused){a.pause();a.removeAttribute('src');a.load();$('#play').textContent='play';stopNP();return;}
  a.src=urlFor(sel.id);a.play().then(()=>{$('#play').textContent='stop';startNP();})
   .catch(e=>{$('#np').textContent='playback failed: '+e.message;});
});
function startNP(){stopNP();poll();npTimer=setInterval(poll,10000);}
function stopNP(){if(npTimer)clearInterval(npTimer);npTimer=null;$('#np').textContent='';}
async function poll(){
  try{const r=await fetch('/nowplaying');if(r.status!==200){$('#np').textContent='';return;}
    const d=await r.json();
    $('#np').textContent=[d.channel,d.artist,d.song].filter(Boolean).join('  ·  ');
  }catch{}
}

fetch('/channels').then(r=>r.json()).then(d=>{all=d;render();})
  .catch(e=>{$('#list').innerHTML='<div class="empty">could not load channels: '+esc(e.message)+
    '<br><br>the proxy may still be authenticating — reload in a moment</div>';});
</script>
</body>
</html>
""";
}

// The Genre field this file reads lives in extras/DecorationsGenre.cs, which is
// compiled into SXMPlayer.Client rather than here — a partial type cannot be
// extended across assembly boundaries.
