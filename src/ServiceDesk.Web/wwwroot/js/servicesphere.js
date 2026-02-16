/* =====================================================================
   ServiceSphere - Custom JavaScript
   Sidebar toggle, dark mode, and UI interactivity
   ===================================================================== */

document.addEventListener('DOMContentLoaded', function () {

    // ----- Elements -----
    const sidebar = document.getElementById('ss-sidebar');
    const sidebarOverlay = document.getElementById('ss-sidebar-overlay');
    const mainContent = document.getElementById('ss-main');
    const sidebarToggleBtn = document.getElementById('sidebar-toggle');
    const darkModeToggle = document.getElementById('dark-mode-toggle');

    // ----- Sidebar Toggle -----
    function toggleSidebar() {
        const isDesktop = window.innerWidth >= 992;

        if (isDesktop) {
            sidebar.classList.toggle('collapsed');
            mainContent.classList.toggle('sidebar-collapsed');
            // Save preference
            localStorage.setItem('ss-sidebar-collapsed', sidebar.classList.contains('collapsed'));
        } else {
            sidebar.classList.toggle('show');
            sidebarOverlay.classList.toggle('show');
        }
    }

    if (sidebarToggleBtn) {
        sidebarToggleBtn.addEventListener('click', toggleSidebar);
    }

    // Close sidebar overlay on click (mobile)
    if (sidebarOverlay) {
        sidebarOverlay.addEventListener('click', function () {
            sidebar.classList.remove('show');
            sidebarOverlay.classList.remove('show');
        });
    }

    // Restore sidebar state on desktop
    if (window.innerWidth >= 992) {
        const isCollapsed = localStorage.getItem('ss-sidebar-collapsed') === 'true';
        if (isCollapsed) {
            sidebar.classList.add('collapsed');
            mainContent.classList.add('sidebar-collapsed');
        }
    }

    // Handle window resize
    window.addEventListener('resize', function () {
        if (window.innerWidth >= 992) {
            sidebar.classList.remove('show');
            sidebarOverlay.classList.remove('show');
        }
    });

    // ----- Dark Mode Toggle -----
    function setTheme(theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
        localStorage.setItem('ss-theme', theme);

        // Update icon
        if (darkModeToggle) {
            const icon = darkModeToggle.querySelector('i');
            if (icon) {
                icon.className = theme === 'dark' ? 'bi bi-sun' : 'bi bi-moon';
            }
        }
    }

    // Restore saved theme
    const savedTheme = localStorage.getItem('ss-theme') || 'light';
    setTheme(savedTheme);

    if (darkModeToggle) {
        darkModeToggle.addEventListener('click', function () {
            const currentTheme = document.documentElement.getAttribute('data-bs-theme');
            setTheme(currentTheme === 'dark' ? 'light' : 'dark');
        });
    }

    // ----- Sidebar Submenu Toggle -----
    document.querySelectorAll('.ss-sidebar .has-submenu > .nav-link').forEach(function (link) {
        link.addEventListener('click', function (e) {
            e.preventDefault();
            const parent = this.parentElement;
            // Close other open menus
            parent.parentElement.querySelectorAll('.has-submenu.menu-open').forEach(function (item) {
                if (item !== parent) {
                    item.classList.remove('menu-open');
                }
            });
            parent.classList.toggle('menu-open');
        });
    });

    // ----- Active Menu Highlighting -----
    const currentPath = window.location.pathname.toLowerCase();
    document.querySelectorAll('.ss-sidebar .nav-link').forEach(function (link) {
        const href = link.getAttribute('href');
        if (href && href !== '#' && href !== 'javascript:void(0)') {
            const linkPath = href.toLowerCase();
            if (currentPath === linkPath || (linkPath !== '/' && currentPath.startsWith(linkPath))) {
                link.classList.add('active');
                // Expand parent submenu if in a submenu
                const parentItem = link.closest('.has-submenu');
                if (parentItem) {
                    parentItem.classList.add('menu-open');
                }
            }
        }
    });

    // ----- Fullscreen Toggle -----
    const fullscreenBtn = document.getElementById('fullscreen-toggle');
    if (fullscreenBtn) {
        fullscreenBtn.addEventListener('click', function () {
            if (!document.fullscreenElement) {
                document.documentElement.requestFullscreen();
            } else {
                document.exitFullscreen();
            }
        });
    }

    // ----- Tooltips Init -----
    const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
    tooltipTriggerList.map(function (tooltipTriggerEl) {
        return new bootstrap.Tooltip(tooltipTriggerEl);
    });

});
