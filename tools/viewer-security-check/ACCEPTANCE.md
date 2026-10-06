# Interactive viewer — manual acceptance

jsdom does no layout and implements no browser shortcuts, so what follows is
checked by hand, on every slice (spec `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`, §7, A14),
in the latest desktop Chrome, Edge and Firefox (Safari: best effort), opening
the generated files directly (`file://`), on two sites:

    dotnet run --project src/OKF4net.Render -c Release -- bundles/acme_retail --out <tmp>/acme-site
    dotnet run --project producers/src/OkfProducer.Cli -c Release -- generate --repo . --out <tmp>/okf4net-bundle --no-msbuild
    dotnet run --project src/OKF4net.Render -c Release -- <tmp>/okf4net-bundle --out <tmp>/okf4net-site

Record the date, browser versions and any failure in the pull request.

## P1

- [ ] A page of the OKF4net site opens without a visible stall; scrolling the explorer stays smooth.
- [ ] Explorer: the current page is highlighted and its ancestors are open; a folder that is also a page opens on its name and expands on its arrow; the filter keeps ancestors of matches.
- [ ] Links from a page three levels deep land on the right pages.
- [ ] Palette: the "Jump to..." button opens it; Ctrl+K and `/` open it from the page body (note what each browser does with these keys when the page has focus and when the address bar has it); Tab stays inside; Escape closes and focus returns; arrows and Enter navigate.
- [ ] Palette with many results (query `o` on the OKF4net site): arrowing past the bottom of the list keeps the active option visible; every result is reachable.
- [ ] Palette with an IME (e.g. Windows Japanese input): confirming a composition with Enter does not navigate, cancelling it with Escape does not close the palette.
- [ ] A screen reader (NVDA or Narrator) announces the palette as a dialog, the active option and the result count, and the theme button's pressed state, including after the system theme changes.
- [ ] Theme: the toggle switches; reloading and opening another page keeps the choice where the browser shares storage between `file://` pages, and never flashes the other theme on load.
- [ ] Contrast is readable in both themes (text, badges, the active palette option).
- [ ] Contents list: clicking an entry lands on the heading; Back and Forward return through the visited headings.
- [ ] Opening `<page>.html#<heading text>` (an author fragment) lands on that heading.
- [ ] At 390 px wide, the zones stack without horizontal scrolling and the palette fits.
