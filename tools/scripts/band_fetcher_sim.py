#!/usr/bin/env python3
"""
Simulates BandPageFetcher's Phase 1 → Phase 2 pattern:
  Phase 1: page 0 for N songs × 3 band types (discover totalPages)
  Phase 2: all remaining pages as flat parallel pool

Usage: python3 band_fetcher_sim.py <access_token> <account_id> [num_songs]
"""

import sys, time, json, asyncio, aiohttp
from collections import defaultdict

EVENTS_BASE = "https://events-public-service-live.ol.epicgames.com"
BAND_TYPES = ["Band_Duets", "Band_Trios", "Band_Quad"]
MAX_PAGES = 100

async def fetch_page(session, token, account_id, song_id, band_type, page, sem):
    url = (f"{EVENTS_BASE}/api/v1/leaderboards/FNFestival/alltime_{song_id}_{band_type}"
           f"/alltime/{account_id}?page={page}&rank=0&appId=Fortnite&showLiveSessions=false")
    headers = {"Authorization": f"Bearer {token}"}
    async with sem:
        start = time.monotonic()
        try:
            async with session.get(url, headers=headers, timeout=aiohttp.ClientTimeout(total=60)) as resp:
                body = await resp.read()
                elapsed = time.monotonic() - start
                if resp.status == 200:
                    data = json.loads(body)
                    return {"status": 200, "ms": int(elapsed*1000),
                            "entries": len(data.get("entries", [])),
                            "totalPages": data.get("totalPages", 0)}
                return {"status": resp.status, "ms": int(elapsed*1000), "entries": 0, "totalPages": 0}
        except Exception as e:
            return {"status": "err", "ms": int(time.monotonic()-start)*1000, "entries": 0, "totalPages": 0}

async def main():
    if len(sys.argv) < 3:
        print("Usage: band_fetcher_sim.py <access_token> <account_id> [num_songs]")
        sys.exit(1)

    token = sys.argv[1]
    account_id = sys.argv[2]
    num_songs = int(sys.argv[3]) if len(sys.argv) > 3 else 20

    # Get song IDs
    async with aiohttp.ClientSession() as session:
        async with session.get("https://festivalscoretracker.com/api/songs") as resp:
            data = await resp.json()
            song_ids = [s["songId"] for s in data.get("songs", [])[:num_songs]]

    sem = asyncio.Semaphore(512)
    total_combos = len(song_ids) * len(BAND_TYPES)

    print(f"=== Phase 1: {total_combos} page-0 requests ({len(song_ids)} songs × {len(BAND_TYPES)} types) ===")

    phase1_start = time.monotonic()
    async with aiohttp.ClientSession() as session:
        # Phase 1: page 0 for all combos
        p0_tasks = []
        for sid in song_ids:
            for bt in BAND_TYPES:
                p0_tasks.append(fetch_page(session, token, account_id, sid, bt, 0, sem))

        p0_results = await asyncio.gather(*p0_tasks)
        phase1_time = time.monotonic() - phase1_start

        ok = sum(1 for r in p0_results if r["status"] == 200)
        errs = sum(1 for r in p0_results if r["status"] != 200)
        avg_ms = sum(r["ms"] for r in p0_results) // len(p0_results) if p0_results else 0
        print(f"  {ok}/{total_combos} OK, {errs} errors, avg={avg_ms}ms, wall={phase1_time:.1f}s")

        # Collect remaining pages to fetch
        remaining = []
        total_pages_all = 0
        for i, (sid, bt) in enumerate((sid, bt) for sid in song_ids for bt in BAND_TYPES):
            r = p0_results[i]
            if r["status"] == 200 and r["totalPages"] > 1:
                tp = min(r["totalPages"], MAX_PAGES)
                total_pages_all += tp
                for p in range(1, tp):
                    remaining.append((sid, bt, p))

        print(f"  Discovered {total_pages_all} total pages, {len(remaining)} remaining to fetch")

        # Phase 2: all remaining pages flat
        print(f"\n=== Phase 2: {len(remaining)} pages (flat parallel, sem=512) ===")
        phase2_start = time.monotonic()

        p2_tasks = [fetch_page(session, token, account_id, sid, bt, pg, sem)
                    for sid, bt, pg in remaining]

        p2_results = await asyncio.gather(*p2_tasks)
        phase2_time = time.monotonic() - phase2_start

        ok2 = sum(1 for r in p2_results if r["status"] == 200)
        errs2 = sum(1 for r in p2_results if r["status"] != 200)
        entries2 = sum(r["entries"] for r in p2_results)
        avg_ms2 = sum(r["ms"] for r in p2_results) // len(p2_results) if p2_results else 0
        rps = len(p2_results) / phase2_time if phase2_time > 0 else 0

        status_counts = defaultdict(int)
        for r in p2_results:
            status_counts[r["status"]] += 1

        print(f"  {ok2}/{len(remaining)} OK, {errs2} errors, avg={avg_ms2}ms")
        print(f"  {entries2:,} entries, {rps:.0f} RPS, wall={phase2_time:.1f}s")
        for s, c in sorted(status_counts.items()):
            if s != 200:
                print(f"  status {s}: {c}")

    total_time = phase1_time + phase2_time
    total_reqs = total_combos + len(remaining)
    print(f"\n=== Summary ===")
    print(f"  Total: {total_reqs:,} requests in {total_time:.1f}s ({total_reqs/total_time:.0f} RPS)")
    print(f"  Phase 1: {phase1_time:.1f}s | Phase 2: {phase2_time:.1f}s")

if __name__ == "__main__":
    asyncio.run(main())
