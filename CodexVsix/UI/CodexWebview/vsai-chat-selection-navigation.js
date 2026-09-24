(function () {
  'use strict';

  const scrollHandlers = new Map();
  const highlightName = 'vsai-chat-selection';

  function findTurn(key) {
    if (typeof key !== 'string' || key.length === 0) return null;
    return Array.from(document.querySelectorAll('[data-content-search-turn-key], [data-turn-key]'))
      .find(element => element.getAttribute('data-content-search-turn-key') === key
        || element.getAttribute('data-turn-key') === key) || null;
  }

  function textOffset(root, container, offset) {
    const prefix = document.createRange();
    prefix.selectNodeContents(root);
    prefix.setEnd(container, offset);
    return prefix.cloneContents().textContent.length;
  }

  function rangeAt(root, start, end) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    const range = document.createRange();
    let node;
    let position = 0;
    let foundStart = false;
    while ((node = walker.nextNode())) {
      const next = position + node.length;
      if (!foundStart && start <= next) {
        range.setStart(node, Math.max(0, start - position));
        foundStart = true;
      }
      if (foundStart && end <= next) {
        range.setEnd(node, Math.max(0, end - position));
        return range;
      }
      position = next;
    }
    return null;
  }

  function capture(text) {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount !== 1 || !text || !selection.toString().trim()) return null;
    const range = selection.getRangeAt(0);
    const element = range.commonAncestorContainer.nodeType === Node.ELEMENT_NODE
      ? range.commonAncestorContainer : range.commonAncestorContainer.parentElement;
    const root = element && element.closest('[data-content-search-turn-key], [data-turn-key]');
    if (!root || !root.contains(range.startContainer) || !root.contains(range.endContainer)) return null;
    const turnKey = root.getAttribute('data-content-search-turn-key') || root.getAttribute('data-turn-key');
    if (!turnKey) return null;
    const start = textOffset(root, range.startContainer, range.startOffset);
    const end = textOffset(root, range.endContainer, range.endOffset);
    if (end <= start) return null;
    return { turnKey, start, end, fragment: (root.textContent || '').slice(start, end) };
  }

  function highlight(range) {
    if (typeof CSS !== 'undefined' && CSS.highlights && typeof Highlight !== 'undefined') {
      if (!document.getElementById('vsai-chat-selection-style')) {
        const style = document.createElement('style');
        style.id = 'vsai-chat-selection-style';
        style.textContent = '::highlight(vsai-chat-selection) { background: #e9b44c; color: #171717; }';
        document.head.appendChild(style);
      }
      CSS.highlights.set(highlightName, new Highlight(range));
      return;
    }
    const selection = window.getSelection();
    if (selection) {
      selection.removeAllRanges();
      selection.addRange(range);
    }
  }

  async function reveal(origin) {
    if (!origin || typeof origin.turnKey !== 'string') return false;
    const scrollToTurn = scrollHandlers.get(origin.conversationId);
    if (!scrollToTurn) return false;
    let root = findTurn(origin.turnKey);
    if (!root) {
      try {
        await scrollToTurn(origin.turnKey);
      } catch (_) {
        return false;
      }
      for (let attempt = 0; attempt < 4 && !root; attempt++) {
        await new Promise(resolve => requestAnimationFrame(resolve));
        root = findTurn(origin.turnKey);
      }
    }
    if (!root) return false;
    const content = root.textContent || '';
    let start = origin.start;
    let end = origin.end;
    if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || end <= start) return false;
    if (content.slice(start, end) !== origin.fragment) {
      const fragment = origin.fragment;
      if (typeof fragment !== 'string' || !fragment) return false;
      let best = -1;
      let distance = Infinity;
      for (let index = content.indexOf(fragment); index !== -1; index = content.indexOf(fragment, index + 1)) {
        if (Math.abs(index - start) < distance) {
          best = index;
          distance = Math.abs(index - start);
        }
      }
      if (best < 0) {
        root.scrollIntoView({ block: 'center', inline: 'nearest' });
        return false;
      }
      start = best;
      end = best + fragment.length;
    }
    const range = rangeAt(root, start, end);
    if (!range) return false;
    const target = range.startContainer.nodeType === Node.ELEMENT_NODE
      ? range.startContainer : range.startContainer.parentElement;
    (target || root).scrollIntoView({ block: 'center', inline: 'nearest' });
    highlight(range);
    return true;
  }

  window.__vsaiChatSelectionNavigation = {
    capture,
    reveal,
    registerScroll(conversationId, handler) {
      if (conversationId != null) scrollHandlers.set(conversationId, handler);
    },
    unregisterScroll(conversationId, handler) {
      if (scrollHandlers.get(conversationId) === handler) scrollHandlers.delete(conversationId);
    }
  };
})();
