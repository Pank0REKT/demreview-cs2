// DemReview — сайт проекта: язык, карусель, скачивание последнего релиза.
(() => {
  const REPO = "Pank0REKT/demreview-cs2";
  const RELEASES_URL = `https://github.com/${REPO}/releases`;
  const root = document.documentElement;

  // ---------- Язык ----------
  // Начальный язык выставляет встроенный скрипт в <head> (без мигания); здесь — переключение и атрибуты.

  const t = (ru, en) => `<span data-lang="ru">${ru}</span><span data-lang="en">${en}</span>`;
  const lang = () => root.lang === "ru" ? "ru" : "en";

  function applyLanguage(value) {
    root.lang = value;
    try { localStorage.setItem("lang", value); } catch { /* приватный режим */ }
    const title = root.dataset[`title${value === "ru" ? "Ru" : "En"}`];
    if (title) document.title = title;
    // Атрибуты (alt, aria-label, title): data-ru-alt / data-en-alt и т. п.
    for (const el of document.querySelectorAll("[data-i18n-attr]")) {
      for (const attr of el.dataset.i18nAttr.split(",")) {
        const text = el.getAttribute(`data-${value}-${attr}`);
        if (text !== null) el.setAttribute(attr, text);
      }
    }
    const toggle = document.querySelector(".lang-toggle");
    if (toggle) {
      toggle.textContent = value === "ru" ? "EN" : "RU";
      toggle.setAttribute("aria-label", value === "ru" ? "Switch to English" : "Переключить на русский");
    }
    document.dispatchEvent(new CustomEvent("languagechange"));
  }

  document.querySelector(".lang-toggle")?.addEventListener("click", () => applyLanguage(lang() === "ru" ? "en" : "ru"));
  applyLanguage(lang());

  // ---------- Карусель ----------

  for (const carousel of document.querySelectorAll("[data-carousel]")) {
    const track = carousel.querySelector(".carousel-track");
    const slides = [...track.children];
    const dots = carousel.querySelector(".carousel-dots");
    const caption = carousel.querySelector(".carousel-caption");
    const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
    let index = 0;
    let timer = null;

    slides.forEach((slide, i) => {
      const dot = document.createElement("button");
      dot.type = "button";
      dot.dataset.i18nAttr = "aria-label";
      dot.setAttribute("data-ru-aria-label", `Скриншот ${i + 1}`);
      dot.setAttribute("data-en-aria-label", `Screenshot ${i + 1}`);
      dot.setAttribute("aria-label", lang() === "ru" ? `Скриншот ${i + 1}` : `Screenshot ${i + 1}`);
      dot.addEventListener("click", () => { go(i); restart(); });
      dots.append(dot);
    });

    function go(i) {
      index = (i + slides.length) % slides.length;
      track.style.transform = `translateX(-${index * 100}%)`;
      slides.forEach((s, n) => s.setAttribute("aria-hidden", String(n !== index)));
      [...dots.children].forEach((d, n) => d.setAttribute("aria-current", String(n === index)));
      caption.innerHTML = slides[index].querySelector("template").innerHTML;
    }

    function restart() {
      clearInterval(timer);
      if (!reduceMotion) timer = setInterval(() => go(index + 1), 6000);
    }
    const stop = () => clearInterval(timer);

    carousel.querySelector(".prev").addEventListener("click", () => { go(index - 1); restart(); });
    carousel.querySelector(".next").addEventListener("click", () => { go(index + 1); restart(); });
    carousel.addEventListener("mouseenter", stop);
    carousel.addEventListener("mouseleave", restart);
    carousel.addEventListener("focusin", stop);
    carousel.addEventListener("focusout", restart);
    carousel.addEventListener("keydown", e => {
      if (e.key === "ArrowLeft") go(index - 1);
      if (e.key === "ArrowRight") go(index + 1);
    });
    document.addEventListener("visibilitychange", () => document.hidden ? stop() : restart());

    // Свайп на сенсорных экранах.
    const viewport = carousel.querySelector(".carousel-viewport");
    let startX = null;
    viewport.addEventListener("pointerdown", e => { if (e.pointerType !== "mouse") startX = e.clientX; });
    viewport.addEventListener("pointerup", e => {
      if (startX === null) return;
      const dx = e.clientX - startX;
      startX = null;
      if (Math.abs(dx) > 40) { go(index + (dx < 0 ? 1 : -1)); restart(); }
    });

    // Увеличение скриншота.
    const lightbox = document.querySelector(".lightbox");
    for (const button of carousel.querySelectorAll(".slide button")) {
      button.addEventListener("click", () => {
        const img = button.querySelector("img");
        const big = lightbox.querySelector("img");
        big.src = button.dataset.full;
        big.alt = img.alt;
        lightbox.showModal();
      });
    }
    lightbox?.addEventListener("click", e => { if (e.target === lightbox) lightbox.close(); });

    go(0);
    restart();
  }

  // ---------- Дорожная карта: блок из адреса (#id) раскрывается, остальные сворачиваются ----------

  const roadmapBlocks = [...document.querySelectorAll(".roadmap-block")];
  if (roadmapBlocks.length) {
    const toggleAll = document.querySelector("[data-roadmap-toggle]");

    function updateToggle() {
      const allOpen = roadmapBlocks.every(b => b.open);
      toggleAll.innerHTML = allOpen ? t("Свернуть все", "Collapse all") : t("Развернуть все", "Expand all");
    }

    function openFromHash() {
      const id = decodeURIComponent(location.hash.slice(1));
      const target = roadmapBlocks.find(b => b.id === id);
      if (!target) return;
      for (const b of roadmapBlocks) {
        b.open = b === target;
        b.classList.toggle("is-target", b === target);
      }
      // После раскрытия высота блоков меняется — прокрутка к цели заново.
      requestAnimationFrame(() => target.scrollIntoView({ block: "start" }));
      updateToggle();
    }

    toggleAll.hidden = false;
    toggleAll.addEventListener("click", () => {
      const open = !roadmapBlocks.every(b => b.open);
      for (const b of roadmapBlocks) b.open = open;
      updateToggle();
    });
    for (const b of roadmapBlocks) b.addEventListener("toggle", updateToggle);
    addEventListener("hashchange", openFromHash);
    openFromHash();
    updateToggle();
  }

  // ---------- Релизы: кнопки скачивания и «Что нового» ----------

  function detectOs() {
    const platform = navigator.userAgentData?.platform || navigator.platform || "";
    const ua = navigator.userAgent;
    if (/android|iphone|ipad/i.test(ua)) return "mobile";
    if (/linux/i.test(platform) || (/linux/i.test(ua) && !/android/i.test(ua))) return "linux";
    if (/mac/i.test(platform)) return "mac";
    return "windows";
  }

  const sizeMb = bytes => (bytes / 1024 / 1024).toFixed(0);
  const icons = {
    download: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M5 21h14"/></svg>',
  };

  async function loadReleases() {
    const cacheKey = "releases-v1";
    try {
      const cached = JSON.parse(sessionStorage.getItem(cacheKey) || "null");
      if (cached && Date.now() - cached.time < 10 * 60 * 1000) return cached.data;
    } catch { /* нет хранилища */ }
    const response = await fetch(`https://api.github.com/repos/${REPO}/releases?per_page=10`,
      { headers: { Accept: "application/vnd.github+json" } });
    if (!response.ok) throw new Error(`GitHub API ${response.status}`);
    const data = (await response.json()).filter(r => !r.draft && !r.prerelease);
    try { sessionStorage.setItem(cacheKey, JSON.stringify({ time: Date.now(), data })); } catch { /* нет хранилища */ }
    return data;
  }

  function renderDownloads(release) {
    const version = release.tag_name.replace(/^v/i, "");
    const asset = suffix => release.assets.find(a => a.name.endsWith(suffix));
    const win = asset("win-x64.zip");
    const linux = asset("linux-x64.tar.gz");
    const os = detectOs();
    const primary = os === "linux" ? linux : win;
    const secondary = os === "linux" ? win : linux;
    const name = a => a === linux ? "Linux" : "Windows";

    for (const block of document.querySelectorAll("[data-download]")) {
      const buttons = block.querySelector(".download-buttons");
      const meta = block.querySelector(".download-meta");
      if (!primary) continue;

      let html = `<a class="btn btn-primary" href="${primary.browser_download_url}">${icons.download}${t(`Скачать для ${name(primary)}`, `Download for ${name(primary)}`)}</a>`;
      if (secondary)
        html += `<a class="btn btn-ghost" href="${secondary.browser_download_url}">${t(name(secondary), name(secondary))}</a>`;
      buttons.innerHTML = html;

      const date = new Date(release.published_at);
      const dateRu = date.toLocaleDateString("ru-RU", { day: "numeric", month: "long", year: "numeric" });
      const dateEn = date.toLocaleDateString("en-US", { day: "numeric", month: "long", year: "numeric" });
      meta.innerHTML =
        t(`Версия ${version} · ${dateRu} · ${sizeMb(primary.size)} МБ · `, `Version ${version} · ${dateEn} · ${sizeMb(primary.size)} MB · `) +
        `<a href="${release.html_url}">${t("заметки к релизу", "release notes")}</a>` +
        (os === "mac" || os === "mobile"
          ? t(" · программа работает на Windows и Linux", " · the app runs on Windows and Linux")
          : "");
    }
  }

  function renderReleaseList(releases) {
    const list = document.querySelector("[data-releases]");
    if (!list) return;
    list.innerHTML = releases.slice(0, 5).map((r, i) => {
      const date = new Date(r.published_at);
      return `<li><a href="${r.html_url}">
        <span><span class="ver">DemReview ${r.tag_name.replace(/^v/i, "")}</span>${i === 0 ? `<span class="latest">${t("последняя", "latest")}</span>` : ""}</span>
        <time datetime="${r.published_at}">${t(date.toLocaleDateString("ru-RU", { day: "numeric", month: "long", year: "numeric" }),
          date.toLocaleDateString("en-US", { day: "numeric", month: "long", year: "numeric" }))}</time>
      </a></li>`;
    }).join("");
  }

  if (document.querySelector("[data-download], [data-releases]")) {
    loadReleases()
      .then(releases => {
        if (releases.length === 0) return;
        renderDownloads(releases[0]);
        renderReleaseList(releases);
      })
      .catch(() => { /* Без API остаются статичные ссылки на страницу релизов. */ });
  }

  // Ссылки «все релизы» по умолчанию ведут на GitHub.
  for (const a of document.querySelectorAll("[data-releases-link]")) a.href = `${RELEASES_URL}/latest`;
})();
