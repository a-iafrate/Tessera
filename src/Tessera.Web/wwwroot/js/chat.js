window.tesseraChat = {
    scrollToBottom: function (elementId) {
        var el = document.getElementById(elementId);
        if (el) {
            el.scrollTop = el.scrollHeight;
        }
    }
};
