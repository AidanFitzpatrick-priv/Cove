(() => {
  const header = document.querySelector(".site-header");
  const features = document.querySelectorAll(".feature-list li");
  const suggestRows = document.querySelectorAll(".suggest-row");
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  const onScroll = () => {
    if (!header) return;
    header.classList.toggle("is-scrolled", window.scrollY > 8);
  };

  onScroll();
  window.addEventListener("scroll", onScroll, { passive: true });

  if (!reduceMotion && suggestRows.length > 1) {
    let index = 0;
    window.setInterval(() => {
      suggestRows.forEach((row) => row.classList.remove("is-active"));
      index = (index + 1) % suggestRows.length;
      suggestRows[index].classList.add("is-active");
    }, 2200);
  }

  if (!("IntersectionObserver" in window)) {
    features.forEach((item) => item.classList.add("is-in"));
    return;
  }

  const observer = new IntersectionObserver(
    (entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) return;
        entry.target.classList.add("is-in");
        observer.unobserve(entry.target);
      });
    },
    { threshold: 0.18, rootMargin: "0px 0px -8% 0px" }
  );

  features.forEach((item, index) => {
    item.style.transitionDelay = `${Math.min(index * 50, 400)}ms`;
    observer.observe(item);
  });
})();
