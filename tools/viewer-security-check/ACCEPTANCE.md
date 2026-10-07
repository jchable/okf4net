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
- [ ] At 390 px wide, the zones stack without horizontal scrolling and the palette fits; a wide GFM table in a page (e.g. acme `tables/orders.html`) scrolls inside its own box instead of widening the page.
- [ ] At 390 px wide, a concept with a long unbroken id and title (e.g. `a-very-long-identifier-without-any-space-at-all-repeated-until-it-overflows`) wraps inside its palette option, and a long unbroken heading wraps inside its contents entry, instead of widening the palette or the page.

## P1.1

Tooled first (spec §12.8): build the two sites with the commands above, run
the recette, and paste its summary into the pull request:

    OKF_PLAYWRIGHT=<path to playwright-core> node tools/viewer-security-check/recette/recette.js \
      --site <tmp>/okf4net-site --acme <tmp>/acme-site --browsers chrome,edge,firefox --slices p1,p1.1

Every result must be `ok` (`RC1`–`RC11`, `fonts`, `H1`–`H13`, `E1`–`E13`,
`C1`–`C8`, `X1`, `X3`, `X4`, `X10`, `X11`, `J1`–`J6`, `L6`, `tokens`,
`requests`, and the second results `<id>-<name>` of the ids that have both a
style probe and a behaviour, e.g. `H7-H7narrow`). Then, by hand:

- [ ] Every capture of `<out>/shots/<browser>/p1.1/` set beside its mockup at 1 440 × 900 (A: `Main.dc.html`; the palette of C: `Focus.dc.html`): every visible difference is an "écart" of spec §11 or a defect.
- [ ] Installed Firefox (not Playwright's), on the OKF4net site's `index.html` and its deepest page: the Network tab shows every `assets/fonts/` request answered (or no font request at all if `okf-fonts.css` serves them), and a paragraph's computed font is Inter.
- [ ] Both themes: chips, explorer flags, type chips and the hourglass read clearly on the page and on the active row (the recette measures the ratios; this is the eye check).
- [ ] At 390 px: the header takes two lines without its counts, the frontmatter box one column, and nothing scrolls sideways.
- [ ] From the top of a page, Tab shows "Skip to content" first; Enter on it, then Tab, lands in the page, past the explorer.
- [ ] Safari (best effort): one page of each site with its fonts and shapes. Safari does not Tab to links by default (Option+Tab does): the recette reports H12 as n/a on WebKit, so do the skip-link line above with Option+Tab.
- IME and screen reader: deferred to issue #177 (A21), not blocking.

- Note : « Global graph » renvoie vers graph.html, écrit par P3 : le lien est mort tant que P3 n'est pas livré.

## P2

*(P2 checks)*

## P3

*(P3 checks)*
