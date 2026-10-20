namespace AllAgent.NET.Core.Browser;

public sealed partial class BrowserAutomationSession
{
    // Extract organic results from rendered DOM, never from a search API or HTML fetch.
    internal const string SearchResultsScript = """
        (() => {
          const blocked = !!document.querySelector('#b_captcha,iframe[src*="captcha"],form[action*="/sorry/"],form[action*="verify"],#captcha,.wappass') ||
            location.hostname.includes('wappass');
          const rows = document.querySelectorAll('#b_results .b_algo, #search .MjjYud, [data-testid="result"], #content_left > div');
          const results = [], seen = new Set();
          for (const row of rows) {
            const a = row.querySelector('h2 a, h3 a, a:has(h3), a[data-testid="result-title-a"]');
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
              // Baidu marks paid links as /baidu.php; organic cards often expose the destination in mu.
              if (parsed.hostname === 'www.baidu.com' && parsed.pathname === '/baidu.php') continue;
              if (parsed.hostname === 'www.baidu.com' && parsed.pathname === '/link' && row.getAttribute('mu')) url = row.getAttribute('mu');
              const target = new URL(url);
              if (!['http:','https:'].includes(target.protocol) || target.username || target.password) continue;
              url = target.href;
            } catch { continue; }
            const title = (a.innerText || a.textContent || row.querySelector('h3')?.innerText || '').trim().slice(0,300);
            if (!title || seen.has(url)) continue;
            seen.add(url);
            const snippet = row.querySelector('.b_caption p, .VwiC3b, [data-result="snippet"], [data-testid="result-snippet"], .c-abstract, .c-font-normal[aria-label], .cos-row');
            const description = (snippet?.getAttribute('aria-label') || snippet?.innerText || '').trim().slice(0,1500);
            results.push({title,url,description});
            if (results.length >= __LIMIT__) break;
          }
          return {blocked,results};
        })()
        """;
}
