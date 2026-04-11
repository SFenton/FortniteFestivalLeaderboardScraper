#!/usr/bin/env python3
"""
Band leaderboard stress test: 8 songs × 3 band types × 100 pages = 2,400 requests.
Simulates the actual scraper's band concurrency pattern.

Usage: python3 band_stress_test.py <access_token> <account_id>
"""

import sys, time, json, asyncio, aiohttp
from collections import defaultdict

EVENTS_BASE = "https://events-public-service-live.ol.epicgames.com"
BAND_TYPES = ["Band_Duets", "Band_Trios", "Band_Quad"]
PAGES = 100
SONGS_CONCURRENT = 8

async def fetch_page(session, token, account_id, song_id, band_type, page, semaphore):
    url = (f"{EVENTS_BASE}/api/v1/leaderboards/FNFestival/alltime_{song_id}_{band_type}"
           f"/alltime/{account_id}?page={page}&rank=0&appId=Fortnite&showLiveSessions=false")
    headers = {"Authorization": f"Bearer {token}"}
    
    async with semaphore:
        start = time.monotonic()
        try:
            async with session.get(url, headers=headers, timeout=aiohttp.ClientTimeout(total=30)) as resp:
                await resp.read()
                elapsed = time.monotonic() - start
                return {"song": song_id[:8], "band": band_type, "page": page, 
                        "status": resp.status, "ms": int(elapsed*1000)}
        except Exception as e:
            elapsed = time.monotonic() - start
            return {"song": song_id[:8], "band": band_type, "page": page,
                    "status": "err", "ms": int(elapsed*1000), "error": str(e)[:50]}

async def main():
    if len(sys.argv) < 3:
        print("Usage: band_stress_test.py <access_token> <account_id>")
        sys.exit(1)
    
    token = sys.argv[1]
    account_id = sys.argv[2]
    
    # Get 8 song IDs from the API
    async with aiohttp.ClientSession() as session:
        async with session.get(f"https://festivalscoretracker.com/api/songs") as resp:
            data = await resp.json()
            songs = data.get("songs", [])
            song_ids = [s["songId"] for s in songs[:SONGS_CONCURRENT]]
    
    total_requests = len(song_ids) * len(BAND_TYPES) * PAGES
    print(f"Stress test: {len(song_ids)} songs × {len(BAND_TYPES)} band types × {PAGES} pages = {total_requests} requests")
    print(f"Concurrency: unlimited (all at once, like the scraper)")
    print()
    
    # No semaphore limit — fire everything at once like the scraper does
    semaphore = asyncio.Semaphore(512)
    
    start = time.monotonic()
    async with aiohttp.ClientSession() as session:
        tasks = []
        for song_id in song_ids:
            for band_type in BAND_TYPES:
                for page in range(PAGES):
                    tasks.append(fetch_page(session, token, account_id, song_id, band_type, page, semaphore))
        
        results = await asyncio.gather(*tasks)
    
    total_time = time.monotonic() - start
    
    # Analyze results
    status_counts = defaultdict(int)
    band_stats = defaultdict(lambda: {"count": 0, "errors": 0, "total_ms": 0, "statuses": defaultdict(int)})
    
    for r in results:
        status_counts[r["status"]] += 1
        bs = band_stats[r["band"]]
        bs["count"] += 1
        bs["total_ms"] += r["ms"]
        bs["statuses"][r["status"]] += 1
        if r["status"] != 200:
            bs["errors"] += 1
    
    print(f"=== Results ({total_time:.1f}s total) ===")
    print(f"Total requests: {len(results)}")
    print(f"RPS: {len(results)/total_time:.0f}")
    print()
    
    print("Status codes:")
    for status, count in sorted(status_counts.items(), key=lambda x: -x[1]):
        print(f"  {status}: {count} ({count*100/len(results):.1f}%)")
    print()
    
    for band_type in BAND_TYPES:
        bs = band_stats[band_type]
        if bs["count"] == 0: continue
        avg_ms = bs["total_ms"] // bs["count"]
        ok = bs["statuses"].get(200, 0)
        print(f"{band_type}: {ok}/{bs['count']} OK, {bs['errors']} errors, avg={avg_ms}ms")
        for status, count in sorted(bs["statuses"].items()):
            if status != 200:
                print(f"  status {status}: {count}")

if __name__ == "__main__":
    asyncio.run(main())
