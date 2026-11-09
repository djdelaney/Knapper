// Knapper landing page: theme toggle and copy buttons. No dependencies.
(function () {
  var root = document.getElementById('k');
  var KEY = 'knapper-theme';

  // Theme: dark by default, whatever the system setting. The toggle switches
  // to light and the choice is remembered. The inline script at the top of
  // <body> applies the saved choice before first paint; this keeps the
  // browser bar colour and native scrollbars in step when it changes.
  var toggle = document.getElementById('theme-toggle');
  var themeColor = document.getElementById('theme-color');
  function label() {
    var t = root.getAttribute('data-theme');
    toggle.setAttribute('aria-label', t === 'dark' ? 'Switch to light theme' : 'Switch to dark theme');
  }
  if (toggle) {
    label();
    toggle.addEventListener('click', function () {
      var next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      document.documentElement.style.colorScheme = next;
      if (themeColor) themeColor.setAttribute('content', next === 'light' ? '#FBFAFE' : '#0E0C14');
      try { localStorage.setItem(KEY, next); } catch (e) {}
      label();
    });
  }

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
