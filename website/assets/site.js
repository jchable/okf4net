// OKF4net website — raw ⇄ rendered toggle for the concept-document hero.
(() => {
  const raw = document.getElementById('pane-raw');
  const rendered = document.getElementById('pane-rendered');
  const bRaw = document.getElementById('btn-raw');
  const bRen = document.getElementById('btn-rendered');
  if (!raw || !rendered || !bRaw || !bRen) return;

  function show(mode) {
    const isRaw = mode === 'raw';
    raw.classList.toggle('visible', isRaw);
    rendered.classList.toggle('visible', !isRaw);
    bRaw.setAttribute('aria-pressed', String(isRaw));
    bRen.setAttribute('aria-pressed', String(!isRaw));
  }
  bRaw.addEventListener('click', () => show('raw'));
  bRen.addEventListener('click', () => show('rendered'));
})();
