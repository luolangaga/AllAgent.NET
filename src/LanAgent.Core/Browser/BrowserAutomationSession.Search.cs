namespace LanAgent.Core.Browser;

public sealed partial class BrowserAutomationSession
{
    // Extract organic results from rendered DOM, never from a search API or HTML fetch.
    internal const string SearchResultsScript = """
        (() => {
          const blocked = !!document.querySelector('#b_captcha,iframe[src*="captcha"],form[action*="/sorry/"]');
          const rows = document.querySelectorAll('#b_results .b_algo, #search .MjjYud, [data-testid="result"]');
          const results = [], seen = new Set();
          for (const row of rows) {
            const a = row.querySelector('h2 a, a:has(h3), a[data-testid="result-title-a"]');
            if (!a) continue;
            let url = a.href;
            try {
              const parsed = new URL(url);
              // Bing wraps organic URLs in /ck/a?u=a1<base64url>.
              if (parsed.hostname.endsWith('.bing.com') && parsed.pathname === '/ck/a') {
                const encoded = parsed.searchParams.get('u');
                if (encoded?.startsWith('a1')) {
                  const b64 = encoded.slice(2).replace(/-/g,'+').replace(/_/g,'/');
                  url = new TextDecoder().decode(Uint8Array.from(atob(b64.padEnd(Math.ceil(b64.length/4)*4,'=')),c=>c.charCodeAt(0)));
                }
              }
              const target = new URL(url);
              if (!['http:','https:'].includes(target.protocol) || target.username || target.password) continue;
              url = target.href;
            } catch { continue; }
            const title = (a.innerText || a.textContent || '').trim().slice(0,300);
            if (!title || seen.has(url)) continue;
            seen.add(url);
            const description = (row.querySelector('.b_caption p, .VwiC3b, [data-result="snippet"], [data-testid="result-snippet"]')?.innerText || '').trim().slice(0,1500);
            results.push({title,url,description});
            if (results.length >= __LIMIT__) break;
          }
          return {blocked,results};
        })()
        """;
}
