#!/usr/bin/env python3
"""
Band leaderboard page fetch timing harness.
Tests two approaches:
  A) Sequential: page 0 first, wait, then remaining pages sequentially
  B) Parallel: all pages at once (current behavior)

Usage: python3 band_fetch_harness.py <access_token> <account_id> [song_id]
"""

import sys, time, json, asyncio, aiohttp

EVENTS_BASE = "https://events-public-service-live.ol.epicgames.com"
BAND_TYPES = ["Band_Duets", "Band_Trios", "Band_Quad"]
MAX_PAGES = 10  # only fetch first 10 pages for testing

async def fetch_page(session, token, account_id, song_id, band_type, page):
    url = (f"{EVENTS_BASE}/api/v1/leaderboards/FNFestival/alltime_{song_id}_{band_type}"
           f"/alltime/{account_id}?page={page}&rank=0&appId=Fortnite&showLiveSessions=false")
    headers = {"Authorization": f"Bearer {token}"}
    start = time.monotonic()
    try:
        async with session.get(url, headers=headers) as resp:
            body = await resp.read()
            elapsed = time.monotonic() - start
            status = resp.status
            if status == 200:
                data = json.loads(body)
                total_pages = data.get("totalPages", 0)
                entries = len(data.get("entries", []))
                return {"page": page, "status": status, "ms": int(elapsed*1000), 
                        "entries": entries, "totalPages": total_pages}
            else:
                return {"page": page, "status": status, "ms": int(elapsed*1000), 
                        "entries": 0, "totalPages": 0}
    except Exception as e:
        elapsed = time.monotonic() - start
        return {"page": page, "status": "error", "ms": int(elapsed*1000), 
                "error": str(e), "entries": 0, "totalPages": 0}

async def test_sequential(session, token, account_id, song_id, band_type):
    """Page 0 first, then remaining pages one at a time."""
    results = []
    start = time.monotonic()
    
    # Page 0 first
    r0 = await fetch_page(session, token, account_id, song_id, band_type, 0)
    results.append(r0)
    total_pages = min(r0["totalPages"], MAX_PAGES)
    
    # Remaining pages sequentially
    for p in range(1, total_pages):
        r = await fetch_page(session, token, account_id, song_id, band_type, p)
        results.append(r)
    
    total_ms = int((time.monotonic() - start) * 1000)
    return results, total_ms

async def test_parallel(session, token, account_id, song_id, band_type, known_pages):
    """All pages at once."""
    pages = min(known_pages, MAX_PAGES)
    start = time.monotonic()
    tasks = [fetch_page(session, token, account_id, song_id, band_type, p) for p in range(pages)]
    results = await asyncio.gather(*tasks)
    total_ms = int((time.monotonic() - start) * 1000)
    return list(results), total_ms

async def test_page0_then_parallel(session, token, account_id, song_id, band_type):
    """Page 0 first, then remaining pages in parallel."""
    results = []
    start = time.monotonic()
    
    # Page 0 first
    r0 = await fetch_page(session, token, account_id, song_id, band_type, 0)
    results.append(r0)
    total_pages = min(r0["totalPages"], MAX_PAGES)
    
    # Remaining pages in parallel
    if total_pages > 1:
        tasks = [fetch_page(session, token, account_id, song_id, band_type, p) 
                 for p in range(1, total_pages)]
        remaining = await asyncio.gather(*tasks)
        results.extend(remaining)
    
    total_ms = int((time.monotonic() - start) * 1000)
    return results, total_ms

async def main():
    if len(sys.argv) < 3:
        print("Usage: band_fetch_harness.py <access_token> <account_id> [song_id]")
        sys.exit(1)
    
    token = sys.argv[1]
    account_id = sys.argv[2]
    # Default: use a popular song (Bohemian Rhapsody)
    song_id = sys.argv[3] if len(sys.argv) > 3 else "9acba4f4-ab64-4971-bb86-fd166ac3471a"
    
    print(f"Song: {song_id}")
    print(f"Max pages per test: {MAX_PAGES}")
    print()
    
    async with aiohttp.ClientSession() as session:
        for band_type in BAND_TYPES:
            print(f"=== {band_type} ===")
            
            # Test A: Sequential (page 0 first)
            print(f"\n  [A] Sequential (page 0 first, then 1-by-1):")
            results_a, time_a = await test_sequential(session, token, account_id, song_id, band_type)
            errors_a = sum(1 for r in results_a if r["status"] != 200)
            print(f"      Page 0: {results_a[0]['ms']}ms (status={results_a[0]['status']}, entries={results_a[0]['entries']}, totalPages={results_a[0]['totalPages']})")
            if len(results_a) > 1:
                page1_plus = [r['ms'] for r in results_a[1:] if r['status'] == 200]
                if page1_plus:
                    print(f"      Pages 1-{len(results_a)-1}: avg={sum(page1_plus)//len(page1_plus)}ms, min={min(page1_plus)}ms, max={max(page1_plus)}ms")
            print(f"      Total: {time_a}ms, errors: {errors_a}/{len(results_a)}")
            
            # Brief pause between tests
            await asyncio.sleep(2)
            
            # Test B: All parallel
            known_pages = results_a[0]["totalPages"]
            if known_pages > 0:
                print(f"\n  [B] All {min(known_pages, MAX_PAGES)} pages parallel:")
                results_b, time_b = await test_parallel(session, token, account_id, song_id, band_type, known_pages)
                errors_b = sum(1 for r in results_b if r["status"] != 200)
                page_times_b = [r['ms'] for r in results_b if r['status'] == 200]
                if page_times_b:
                    print(f"      Per-page: avg={sum(page_times_b)//len(page_times_b)}ms, min={min(page_times_b)}ms, max={max(page_times_b)}ms")
                print(f"      Total: {time_b}ms, errors: {errors_b}/{len(results_b)}")
                for r in results_b:
                    if r["status"] != 200:
                        print(f"      !! Page {r['page']}: status={r['status']} ({r['ms']}ms)")
            
            await asyncio.sleep(2)
            
            # Test C: Page 0 first, then remaining in parallel
            if known_pages > 1:
                print(f"\n  [C] Page 0 first, then {min(known_pages, MAX_PAGES)-1} parallel:")
                results_c, time_c = await test_page0_then_parallel(session, token, account_id, song_id, band_type)
                errors_c = sum(1 for r in results_c if r["status"] != 200)
                print(f"      Page 0: {results_c[0]['ms']}ms")
                page1_plus_c = [r['ms'] for r in results_c[1:] if r['status'] == 200]
                if page1_plus_c:
                    print(f"      Pages 1-{len(results_c)-1} (parallel): avg={sum(page1_plus_c)//len(page1_plus_c)}ms, min={min(page1_plus_c)}ms, max={max(page1_plus_c)}ms")
                print(f"      Total: {time_c}ms, errors: {errors_c}/{len(results_c)}")
            
            print()
            await asyncio.sleep(3)

if __name__ == "__main__":
    asyncio.run(main())
