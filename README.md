# Fortnite Festival Score Tracker

A simple Windows utility for retrieving your highest Fortnite Festival scores across all instruments and across all seasons!

<img width="835" alt="image" src="https://github.com/user-attachments/assets/9c9f8213-8f5e-401f-a925-d26d1824f47b">
<img width="833" alt="image" src="https://github.com/user-attachments/assets/ca5dbd34-a7be-41c5-932c-57b9e9bc527b">



## Why?

The Fortnite Festival leaderboards reset at the beginning of every season. This means that, while Epic is still saving your highest scores from previous seasons in their leaderboards (actually, every single session you've played!), they aren't surfaced in the game anywhere, most importantly on the song select screen, as they have been for the past... nearly twenty years. I wanted to make this tool because I worked hard for the FCs I've gotten, and it's kind of maddening that I didn't have a foolproof in-game way to track that across seasons.

This app will also output scores you have for songs you don't own, which is a great way to track Battle Pass/Festival Pass/event songs that you missed out on. I don't think that information will ever be surfaced in game if you're playing solo/don't own those tracks and want to see your scores.

## How does the app work?

By supplying the application with an Exchange Token, we simulate the Epic authentication process and spoof being a Fortnite client. This gives us special permissions to read the Fortnite Festival leaderboards, and we query each song, season, and instrument, find your highest scores, and write them out to an Excel workbook that is easily digestible.

## What is an exchange token?

It is a special token required to authenticate with Epic's servers.

## Why do we need to authenticate?

Ideally, we'd just need your account ID, and leaderboards would be public and we could just make a GET request. Unfortunately, not only is leaderboard access locked behind authentication, but your authenticated token must also have special permissions that allow us to access the leaderboard API.

## Does this mean the app could potentially do other things with my account?

Yes. I highly encourage you to read the source code and build the app from scratch yourself to avoid any possibility of this happening. This code will always be open source and available, and while I will provide binaries for those who don't wish to build it themselves, I want to be clear that having your Epic account authenticated could *potentially* mean it could be used for purposes other than this. Always be wary of the executables you download.

## Can I be banned for this?

I won't say no, but I think the chances are not high. We spoof being the PC client and are only querying the leaderboards, though it's possible Harmonix and/or Epic may find it... *odd* that your account is querying every song for every season and instrument, in a way that is not supported in game.

## Is there any other way to see my scores?

FortniteTracker has I think the top 200 scores for each song and instrument, so if you're up there, you're good. If not, no, outside of in-game seeing the current season.

## Can I see band scores?

In theory this is possible, but I haven't figured it out yet. The good news is, the app still pulls your individual scores, even from band runs!

## I'm having an issue. What can I do?

Please file a bug report or feature request! I'll be watching!

# How to run the app

Log in to Epic Games on your web browser of choice. Then, in the app, click "Generate Exchange Code" and that link will open in your browser. Copy the "code" value over to the app, and then click Run. You will see the console output with all the information that the app is querying. The app will run, and drop a file called "FortniteFestivalScores.xlsx" in the executing directory. These are all your scores- including scores on songs you don't own!

Note that exchange tokens from the Epic site expire after about 300 seconds, and once an exchange token is authenticated, you cannot use it again. If you need to run the tool multiple times, you'll need to generate a new exchange token each time.

## Can I play Fortnite while the tool runs?

No. This will cause a new client to authenticate, rendering your previous token useless, and will cause the application to stop working. This is applicable to all platforms- you cannot play Fortnite on *any* platform while this utility runs, or the utility will fail.

## Can I grab only certain songs?

Yes! Head over to the "Select Songs" tab, and select the songs you want to retrieve scores for. If no songs are selected, we will grab all scores for all songs by default.

## Can I grab only certain instruments?

Yes! Head over to the "Options" tab, and toggle on/off the instruments you want to retrieve/write out to the output workbook. Note that you need to have at least one instrument selected to run the tool.

## Can I change the output sort of data?

Yes! Head over to the "Options" tab, where you can change how your output workbook is sorted.
