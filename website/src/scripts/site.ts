// All client-side behaviour for the landing page. Everything here is progressive
// enhancement: the page is fully readable and usable without JavaScript.

const root = document.documentElement;
const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
const os = root.dataset.os ?? 'other';

// ---- header: solid background once scrolled; mobile menu ------------------------
const header = document.querySelector<HTMLElement>('[data-header]');
const menuBtn = document.querySelector<HTMLButtonElement>('[data-menu]');
const onScroll = () => header?.classList.toggle('is-scrolled', window.scrollY > 12);
onScroll();
window.addEventListener('scroll', onScroll, { passive: true });

function setMenu(open: boolean) {
  header?.classList.toggle('is-open', open);
  menuBtn?.setAttribute('aria-expanded', String(open));
}
menuBtn?.addEventListener('click', () => setMenu(menuBtn.getAttribute('aria-expanded') !== 'true'));
document.querySelectorAll('#site-nav a').forEach((a) => a.addEventListener('click', () => setMenu(false)));
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && header?.classList.contains('is-open')) {
    setMenu(false);
    menuBtn?.focus();
  }
});

// ---- reveal on scroll ----------------------------------------------------------
const revealEls = document.querySelectorAll<HTMLElement>('[data-reveal]');
if (reduceMotion.matches || !('IntersectionObserver' in window)) {
  revealEls.forEach((el) => el.classList.add('is-in'));
} else {
  const io = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-in');
          io.unobserve(entry.target);
        }
      }
    },
    { rootMargin: '0px 0px -8% 0px', threshold: 0.08 },
  );
  revealEls.forEach((el) => io.observe(el));
}

// ---- hero parallax (illustration layers only) -----------------------------------
const layers = [...document.querySelectorAll<HTMLElement>('.hero [data-depth]')];
const heroEl = document.querySelector<HTMLElement>('.hero');
if (layers.length && heroEl && !reduceMotion.matches) {
  let ticking = false;
  const update = () => {
    ticking = false;
    const y = window.scrollY;
    if (y > heroEl.offsetHeight * 1.2) return;
    for (const l of layers) {
      const d = Number(l.dataset.depth ?? 0);
      l.style.transform = `translate3d(0, ${(y * d).toFixed(1)}px, 0)`;
    }
  };
  window.addEventListener(
    'scroll',
    () => {
      if (!ticking) {
        ticking = true;
        requestAnimationFrame(update);
      }
    },
    { passive: true },
  );
}

// ---- hero video: respect reduced motion ------------------------------------------
const video = document.querySelector<HTMLVideoElement>('[data-hero-video]');
if (video) {
  const sync = () => {
    if (reduceMotion.matches) video.pause();
    else video.play().catch(() => {});
  };
  sync();
  reduceMotion.addEventListener?.('change', sync);
}

// ---- hero CTA: name the visitor's platform when a build exists -------------------
const cta = document.querySelector<HTMLAnchorElement>('[data-hero-cta]');
const ctaLabel = cta?.querySelector('[data-hero-cta-label]');
if (cta && ctaLabel) {
  if (os === 'macos' && cta.dataset.hasMacos) ctaLabel.textContent = 'Download for macOS';
  else if (os === 'windows' && cta.dataset.hasWindows) ctaLabel.textContent = 'Download for Windows';
}

// ---- copy buttons -----------------------------------------------------------------
document.querySelectorAll<HTMLButtonElement>('[data-copy]').forEach((btn) => {
  btn.addEventListener('click', async () => {
    const text = btn.dataset.copy ?? '';
    let ok = false;
    try {
      await navigator.clipboard.writeText(text);
      ok = true;
    } catch {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      ok = document.execCommand('copy');
      ta.remove();
    }
    const label = btn.querySelector('[data-copy-label]');
    if (!label) return;
    label.textContent = ok ? 'Copied' : 'Press ⌘C';
    btn.toggleAttribute('data-copied', ok);
    setTimeout(() => {
      label.textContent = 'Copy';
      btn.removeAttribute('data-copied');
    }, 1800);
  });
});

// ---- install tabs (WAI-ARIA tabs pattern); default to the visitor's OS -------------
document.querySelectorAll<HTMLElement>('[data-tabs]').forEach((tabs) => {
  const buttons = [...tabs.querySelectorAll<HTMLButtonElement>('[role="tab"]')];
  const panels = [...tabs.querySelectorAll<HTMLElement>('[role="tabpanel"]')];
  const select = (key: string, focus = false) => {
    for (const b of buttons) {
      const on = b.dataset.tab === key;
      b.setAttribute('aria-selected', String(on));
      b.tabIndex = on ? 0 : -1;
      if (on && focus) b.focus();
    }
    for (const p of panels) p.hidden = p.dataset.panel !== key;
  };
  select(os === 'windows' ? 'windows' : 'macos');
  buttons.forEach((b, i) => {
    b.addEventListener('click', () => select(b.dataset.tab!));
    b.addEventListener('keydown', (e) => {
      const dir = e.key === 'ArrowRight' ? 1 : e.key === 'ArrowLeft' ? -1 : 0;
      if (!dir) return;
      e.preventDefault();
      select(buttons[(i + dir + buttons.length) % buttons.length].dataset.tab!, true);
    });
  });
});

// ---- gallery lightbox ---------------------------------------------------------------
const dialog = document.querySelector<HTMLDialogElement>('[data-lightbox]');
const shotButtons = [...document.querySelectorAll<HTMLButtonElement>('[data-gallery] .shot-btn')];
if (dialog && shotButtons.length && typeof dialog.showModal === 'function') {
  const img = dialog.querySelector<HTMLImageElement>('[data-lb-img]')!;
  const cap = dialog.querySelector<HTMLElement>('[data-lb-cap]')!;
  const count = dialog.querySelector<HTMLElement>('[data-lb-count]')!;
  const prev = dialog.querySelector<HTMLButtonElement>('[data-lb-prev]')!;
  const next = dialog.querySelector<HTMLButtonElement>('[data-lb-next]')!;
  let index = 0;
  let opener: HTMLElement | null = null;
  const single = shotButtons.length < 2;
  prev.hidden = single;
  next.hidden = single;

  const show = (i: number) => {
    index = (i + shotButtons.length) % shotButtons.length;
    const b = shotButtons[index];
    img.src = b.dataset.src!;
    img.alt = b.dataset.alt ?? '';
    cap.textContent = b.dataset.caption || b.dataset.alt || '';
    count.textContent = `${index + 1} / ${shotButtons.length}`;
    // warm the neighbours so arrowing is instant
    for (const j of [index + 1, index - 1]) {
      const n = shotButtons[(j + shotButtons.length) % shotButtons.length];
      const pre = new Image();
      pre.src = n.dataset.src!;
    }
  };
  shotButtons.forEach((b, i) =>
    b.addEventListener('click', () => {
      opener = b;
      show(i);
      dialog.showModal();
    }),
  );
  prev.addEventListener('click', () => show(index - 1));
  next.addEventListener('click', () => show(index + 1));
  dialog.querySelector('[data-lb-close]')!.addEventListener('click', () => dialog.close());
  dialog.addEventListener('click', (e) => {
    if (e.target === dialog) dialog.close();
  });
  dialog.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowRight') show(index + 1);
    if (e.key === 'ArrowLeft') show(index - 1);
  });
  dialog.addEventListener('close', () => opener?.focus());
  let startX = 0;
  dialog.addEventListener('pointerdown', (e) => (startX = e.clientX), { passive: true });
  dialog.addEventListener(
    'pointerup',
    (e) => {
      const dx = e.clientX - startX;
      if (Math.abs(dx) > 50 && !single) show(index + (dx < 0 ? 1 : -1));
    },
    { passive: true },
  );
}
