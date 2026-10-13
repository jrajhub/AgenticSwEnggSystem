# AgenticSwEnggSystem
Code for URL shortner workflow  using .NET and a react app to demonstrate it.

How to use it

Terminal 1: start the API (from the AgenticSdlc folder, after replacing it with the new zip):

bash
dotnet run --project src/UrlShortener.Api --urls http://localhost:5080

Terminal 2: start the React app (from the control-room folder):

bash
npm install
npm run dev

Open http://localhost:5173, paste a URL such as https://example.com/anything, and click Shorten. Open the short link in a new tab, then click Refresh clicks to see the count go up.

Things to know
Public addresses only: the API refuses localhost and private network addresses, so use a public URL like https://example.com.
Same address and port: the "API address" box defaults to http://localhost:5080. If you use another port, change it there and it is remembered.
Links are lost on restart: they live in the API's memory. After a restart your list still shows them, but their stats will fail until you create them again.
.NET target: the API needs the net10.0 retarget from earlier.
