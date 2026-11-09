// Knapper landing page: theme toggle and copy buttons. No dependencies.
(function () {
  var root = document.getElementById('k');
  var KEY = 'knapper-theme';

  // Theme: follows the system setting until the visitor picks one with the toggle.
  var toggle = document.getElementById('theme-toggle');
  function label() {
    var t = root.getAttribute('data-theme');
    toggle.setAttribute('aria-label', t === 'dark' ? 'Switch to light theme' : 'Switch to dark theme');
  }
  if (toggle) {
    label();
    toggle.addEventListener('click', function () {
      var next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      try { localStorage.setItem(KEY, next); } catch (e) {}
      label();
    });
  }
  try {
    var mq = window.matchMedia('(prefers-color-scheme: light)');
    mq.addEventListener('change', function (e) {
      var saved = null;
      try { saved = localStorage.getItem(KEY); } catch (err) {}
      if (!saved) { root.setAttribute('data-theme', e.matches ? 'light' : 'dark'); label(); }
    });
  } catch (e) {}

  // Copy buttons: copy data-copy if present, otherwise the frame's <pre> text.
  document.querySelectorAll('.copy').forEach(function (btn) {
    btn.addEventListener('click', function () {
      var text = btn.getAttribute('data-copy');
      if (!text) {
        var frame = btn.closest('.frame');
        var pre = frame && frame.querySelector('pre');
        text = pre ? pre.innerText : '';
      }
      var done = function () {
        var old = btn.textContent;
        btn.textContent = 'Copied';
        setTimeout(function () { btn.textContent = old; }, 1500);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(done, function () {});
      }
    });
  });
})();
