namespace Cove;

static class WebScripts
{
    public const string Bootstrap = """
        (() => {
          if (!window.__cove) {
          window.__cove = true;
          window.__coveAdsOn = true;
          const tick = () => {
            if (!window.__coveAdsOn) return;
            const host = location.hostname;
            if (host === "youtube.com" || host.endsWith(".youtube.com")) {
              const btn = document.querySelector(".ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern");
              if (btn) btn.click();
              const ad = document.querySelector(".ad-showing video");
              if (ad && Number.isFinite(ad.duration)) ad.currentTime = ad.duration;
            }
          };
          setInterval(tick, 500);
          const reportHere = () => chrome.webview?.postMessage({ kind: "here", url: location.href, title: document.title });
          const wrapHistory = (name) => {
            const original = history[name];
            if (typeof original !== "function") return;
            history[name] = function (...args) {
              const result = original.apply(this, args);
              reportHere();
              return result;
            };
          };
          wrapHistory("pushState");
          wrapHistory("replaceState");
          window.addEventListener("popstate", reportHere);
          document.addEventListener("yt-navigate-finish", reportHere);
          document.addEventListener("submit", (e) => {
            const form = e.target;
            if (!(form instanceof HTMLFormElement)) return;
            const pass = form.querySelector("input[type=password]");
            if (!pass || !pass.value) return;
            const user = form.querySelector("input[type=email], input[type=text], input[name*=user i], input[name*=mail i], input[autocomplete=username]");
            chrome.webview?.postMessage({
              kind: "login",
              url: location.href,
              username: user ? user.value : "",
              password: pass.value
            });
          }, true);
          window.__coveFill = (user, pass) => {
            const set = (el, value) => {
              if (!el) return;
              el.focus();
              el.value = value;
              el.dispatchEvent(new Event("input", { bubbles: true }));
              el.dispatchEvent(new Event("change", { bubbles: true }));
            };
            const passEl = document.querySelector("input[type=password]");
            if (!passEl) return false;
            const scope = passEl.form || document;
            const userEl = scope.querySelector("input[type=email], input[type=text], input[name*=user i], input[name*=mail i], input[autocomplete=username]");
            set(userEl, user || "");
            set(passEl, pass || "");
            return true;
          };
          }

          if (window.__covePop) return;
          const pop = document.createElement("button");
          pop.type = "button";
          pop.title = "Pop out video";
          pop.setAttribute("aria-label", "Pop out video");
          const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
          svg.setAttribute("width", "16");
          svg.setAttribute("height", "16");
          svg.setAttribute("viewBox", "0 0 16 16");
          svg.setAttribute("fill", "none");
          const rect = document.createElementNS("http://www.w3.org/2000/svg", "rect");
          rect.setAttribute("x", "1.4");
          rect.setAttribute("y", "4.2");
          rect.setAttribute("width", "8.4");
          rect.setAttribute("height", "8.4");
          rect.setAttribute("rx", "1.3");
          rect.setAttribute("stroke", "white");
          rect.setAttribute("stroke-width", "1.4");
          const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
          path.setAttribute("d", "M7.2 2.2H13.8V8.8M13.8 2.2L7.4 8.6");
          path.setAttribute("stroke", "white");
          path.setAttribute("stroke-width", "1.4");
          path.setAttribute("stroke-linejoin", "round");
          svg.append(rect, path);
          pop.appendChild(svg);
          const popStyle = pop.style;
          const paint = (name, value) => popStyle.setProperty(name, value, "important");
          paint("position", "fixed");
          paint("z-index", "2147483647");
          paint("width", "36px");
          paint("height", "28px");
          paint("margin", "0");
          paint("padding", "0");
          paint("border", "1px solid rgba(255,255,255,.35)");
          paint("border-radius", "8px");
          paint("background", "rgba(12,12,14,.82)");
          paint("display", "none");
          paint("align-items", "center");
          paint("justify-content", "center");
          paint("cursor", "pointer");
          paint("transform", "translateX(-50%)");
          paint("box-shadow", "0 1px 5px rgba(0,0,0,.5)");
          paint("pointer-events", "auto");
          pop.addEventListener("pointerover", () => paint("background", "rgba(12,12,14,.96)"));
          pop.addEventListener("pointerout", () => paint("background", "rgba(12,12,14,.82)"));
          ["pointerdown", "mousedown", "click", "pointerup", "mouseup"].forEach((type) => {
            pop.addEventListener(type, (event) => {
              event.preventDefault();
              event.stopPropagation();
              if (type === "click") chrome.webview?.postMessage({ kind: "popout" });
            }, true);
          });
          (document.documentElement || document.body).appendChild(pop);

          const allVideos = () => {
            const found = [...document.querySelectorAll("video")];
            document.querySelectorAll("ytd-player, #movie_player, .html5-video-player").forEach((el) => {
              if (el.shadowRoot) found.push(...el.shadowRoot.querySelectorAll("video"));
            });
            return found;
          };
          const largestVideo = () => {
            const videos = allVideos().filter((video) => {
              const box = video.getBoundingClientRect();
              return box.width >= 180 && box.height >= 100 && box.bottom > 0 && box.right > 0 && box.top < window.innerHeight && box.left < window.innerWidth;
            });
            videos.sort((a, b) => (b.clientWidth * b.clientHeight) - (a.clientWidth * a.clientHeight));
            return videos[0] || null;
          };
          const playerFor = (video) => {
            let el = video;
            while (el) {
              if (el.classList?.contains("html5-video-player") || el.id === "movie_player") return el;
              const root = el.getRootNode?.();
              const next = el.parentElement || root?.host || null;
              if (!next || next === el) break;
              el = next;
            }
            return video;
          };
          const hits = (x, y, box) => box.width > 0 && box.height > 0 && x >= box.left && x <= box.right && y >= box.top && y <= box.bottom;
          let cursorX = -1;
          let cursorY = -1;
          const placePop = () => {
            const video = largestVideo();
            if (!video || cursorX < 0) { paint("display", "none"); return; }
            const player = playerFor(video);
            const area = player.getBoundingClientRect();
            const button = pop.getBoundingClientRect();
            const showing = popStyle.display !== "none";
            const over = hits(cursorX, cursorY, area) || (showing && hits(cursorX, cursorY, button));
            if (!over) { paint("display", "none"); return; }
            const fullscreen = document.fullscreenElement;
            const host = fullscreen && fullscreen.contains(video) ? fullscreen : (document.documentElement || document.body);
            if (pop.parentElement !== host) host.appendChild(pop);
            const box = video.getBoundingClientRect();
            const hostBox = host.getBoundingClientRect();
            const fixed = host === document.documentElement || host === document.body;
            paint("position", fixed ? "fixed" : "absolute");
            paint("top", ((fixed ? box.top : box.top - hostBox.top) + 10) + "px");
            paint("left", ((fixed ? box.left : box.left - hostBox.left) + box.width / 2) + "px");
            paint("display", "flex");
            paint("visibility", "visible");
            paint("opacity", "1");
          };
          document.addEventListener("pointermove", (event) => {
            cursorX = event.clientX;
            cursorY = event.clientY;
            placePop();
          }, true);
          document.addEventListener("pointerleave", () => { cursorX = -1; paint("display", "none"); }, true);
          document.addEventListener("fullscreenchange", () => {
            chrome.webview?.postMessage({ kind: "fullscreen", on: !!document.fullscreenElement });
            placePop();
          });
          window.addEventListener("scroll", placePop, true);
          window.__covePop = true;
        })();
        """;

    public const string Popout = """
        (async () => {
          if (document.pictureInPictureElement) {
            await document.exitPictureInPicture();
            return { ok: true, mode: "exit" };
          }
          const videos = [...document.querySelectorAll("video")].filter(v => v.videoWidth || v.clientWidth || v.currentSrc || v.src);
          if (!videos.length) return { ok: false, reason: "none" };
          videos.sort((a, b) => (b.clientWidth * b.clientHeight) - (a.clientWidth * a.clientHeight));
          const v = videos[0];
          try {
            if (document.pictureInPictureEnabled && !v.disablePictureInPicture) {
              await v.requestPictureInPicture();
              return { ok: true, mode: "pip" };
            }
          } catch (e) {}
          const src = v.currentSrc || v.src || "";
          if (src && !src.startsWith("blob:") && !src.startsWith("mediasource:")) {
            return { ok: true, mode: "src", src, time: v.currentTime || 0 };
          }
          return { ok: false, reason: "blocked" };
        })()
        """;
}
