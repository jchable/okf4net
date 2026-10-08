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

Every result must be `ok` (`RC1`–`RC11`, `fonts`, `H1`–`H9`, `H11`–`H13`
(H9's graph-page state and H10 are the graph page's: the p3 slice measures them), `E1`–`E13`, `C1`–`C8`, `X1`, `X3`,
`X4`, `X10`, `X11`, `J1`–`J6`, `L6`, `tokens`, `requests`, and the second results `<id>-<name>` of the ids that have both a
style probe and a behaviour, e.g. `H7-H7narrow`). Then, by hand:

- [ ] Every capture of `<out>/shots/<browser>/p1.1/` set beside its mockup at 1 440 × 900 (A: `Main.dc.html`; the palette of C: `Focus.dc.html`): every visible difference is an "écart" of spec §11 or a defect.
- [ ] Installed Firefox (not Playwright's), on the OKF4net site's `index.html` and its deepest page: the Network tab shows every `assets/fonts/` request answered, and a paragraph's computed font is Inter.
- [ ] Both themes: chips, explorer flags, type chips and the hourglass read clearly on the page and on the active row (the recette measures the ratios; this is the eye check).
- [ ] At 390 px: the header takes two lines without its counts, the frontmatter box one column, and nothing scrolls sideways.
- [ ] From the top of a page, Tab shows "Skip to content" first; Enter on it, then Tab, lands in the page, past the explorer.
- [ ] Safari (best effort): one page of each site with its fonts and shapes. Safari does not Tab to links by default (Option+Tab does): the recette reports H12 as n/a on WebKit, so do the skip-link line above with Option+Tab.
- IME and screen reader: deferred to issue #177 (A21), not blocking.

- Note: "Global graph" opens graph.html, written since P3; the p3 slice below measures its state there (H9) and "Reading view" (H10).

## P2

`recette/recette.js --slices p2` measures each line below in Chrome, Edge and Firefox (ids are its result keys, beside §11's X5–X9); what it reports `n/a` (`pass: null`, with its note) is checked by hand.

- [ ] **P2-1** On a concept page with neighbours (acme `computations/gross-margin-period.html`), "Neighbourhood" sits between "On this page" and "Referenced by"; clicking a neighbour opens its page in `file://`; clicking an absent concept (red dashed circle) does nothing.
- [ ] **P2-2** "1 hop" is pressed on load and after a reload; "2 hops" adds the second ring, arrows point at their targets, links into the page are dashed as the legend says.
- [ ] **P2-3** Shapes and lines contrast at least 3:1 and labels and the legend 4.5:1 with the page, in the light and the dark theme; switching the theme redraws nothing and keeps the graph readable.
- [ ] **P2-4** At 390 px wide, the frame fits the panel without horizontal scrolling (a label at the frame's edge may be clipped; the list names it in full).
- [ ] **P2-5** No request leaves the site while the local graph loads or redraws.
- [ ] **P2-6** "Open in graph" opens the graph page selected on this concept (graph.html is written since P3: the recette follows the link and compares the fragment).
- [ ] **P2-7** Keyboard: Tab from "2 hops" never stops inside the drawing; it reaches "+N omitted" (when shown), "Open in graph", then "List · N neighbours", which Enter opens, and each row.
- [ ] **P2-8** Side by side at 1440 × 900 with mockup A's Neighbourhood section (captures `shots/<browser>/p2/X5.png` … `X9.png` under the recette's `--out`): toggle, frame, shapes, labels, edges and foot match, or the difference is one of the écarts recorded in the P2 plan's "Fidélité maquette".

## P3

`recette/recette.js --slices p3` measures G2–G19, H9 and H10 on the graph page, the history under `file://` (control 13, `C13-history`) and the requests leaving the site (control 14, `C14-requests`), in Chrome, Edge, Firefox and WebKit; ids are its result keys. Run it first, record its results and captures in the pull request:

    node tools/viewer-security-check/recette/recette.js --site <okf4net-site> --acme <acme-site> --out <tmp> --slices p3

(Playwright resolved through `OKF_PLAYWRIGHT`, never installed in the repo.) Two optional sites cover what neither official site can: `OKF_RECETTE_GHOST=<dir>` (a render of `tools/viewer-security-check/fixtures/hostile-bundle`, which has broken links: ghost nodes, G12–G13, hostile fragments) and `OKF_RECETTE_BIG=<dir>` (a render of a bundle of more than 1 500 concepts, two types or more and more than 12 tags: the list above the node limit, G9, G6's "Show all tags"); the harness README says how to build them. Without them those checks report `n/a`. Then, by hand:

- [ ] **P3-1** OKF4net site, graph page: the layout settles without freezing the page; note `P3-layout-time` (ms, nodes, longest gap between two frames) for the `NODE_LIMIT` / slice calibration (§4.2, R16); panning and zooming stay smooth during and after the layout.
- [ ] **P3-2** Side by side with mockup B (`GraphFirst.dc.html`) at 1440 × 900 (captures `shots/<browser>/p3/graph-light.png`, `graph-selected.png`, `graph-dark.png`, `graph-ghost.png` under the recette's `--out`): facets, status line, zoom box, nodes, edges, selection, legend and drawer match §11.5, apart from the approved deviations (sandglass in Freshness G5, palette button G1, grey tag chips G6, List button G10) and the revision 11 ones (labels cut at 24 code points, Fit capped at 1.25×).
- [ ] **P3-3** Firefox (R20: a scripted drag was unusable there in an earlier session; `G16` now drives it in every browser, and `G16-drag-during-layout` the drag that stops the layout): if the recette reports either as failed in Firefox, check by hand: dragging the background pans; dragging a node moves it alone and does not select it; a drag during the layout stops it where it is.
- [ ] **P3-4** Keyboard only, from the address bar (the recette's `G14-keyboard` drives it from the zoom box): Tab reaches the facets, then the graph as a single stop, then the drawer; Page Down / Page Up / Home / End walk every node in index order, both components and the isolated node included; the arrows follow links, and the same arrow pressed again cycles through the neighbours in its direction, so every neighbour is reachable; Space selects (the selected node is announced as current); Enter opens the page; a node brought into view by the keyboard is never left outside or under the edge of the drawing; unchecking the focused node's type from the keyboard leaves the focus on the checkbox, and Tab back into the graph lands on the next node.
- [ ] **P3-5** From a concept page, "Global graph" opens the graph with that concept selected and centred; clicking other nodes does not add history entries; Back returns to the concept page; "Reading view" leads to the selected concept.
- [ ] **P3-6** At 390 px: the facets fold into "Filters" above the drawing, the drawer is below it, nothing scrolls sideways (captures `graph-390.png`, `graph-390-selected.png`).
- [ ] **P3-7** Dark theme: nodes, edges, selection and drawer stay legible (the recette measures shape and edge contrast ≥ 3:1 and label contrast ≥ 4.5:1, `G11-contrast`).
- [ ] **P3-8** Safari (best effort): the page loads and the layout ends. Safari does not Tab to links by default: the drawer's links are reached with Option+Tab.

- [ ] **P3-css-1** Real browser (Chrome and Firefox), OKF4net site (806 nodes: under the limit, so the list is the "List" toggle's; the over-limit list is the `OKF_RECETTE_BIG` site's), graph.html with a fragment naming a concept far down the list (the last page of the index, e.g. `#packages/yamlhostileprobe`), at 1440 and at 390 px wide: the marked list entry is inside the visible list once it has rendered, and stays there. The recette's `P3-css-1` and `P3-css-1-over-limit` measure it. A `content-visibility` rule on the list items once pushed it a screen below the fold in Firefox, which no jsdom case can see.
- [ ] **P3-css-2** Real browser (Chrome, Firefox, Safari), acme graph.html: a mouse click on a node shows no dashed keyboard contour around it; Tab onto a node, and an arrow key between nodes, show it (square, side + 20, dashed 3 3, ink). Only a real browser tells the two apart (`:focus-visible`); the recette's `P3-css-2` measures it.
