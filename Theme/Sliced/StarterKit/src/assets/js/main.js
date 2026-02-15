/*
-------------------------------------------------------------------------
* Template Name    : Sliced - Tailwind CSS Admin & Dashboard Template   * 
* Author           : SRBThemes                                          *
* Version          : 3.0.0                                              *
* Created          : May 2024                                           *
* File Description : Main Js file of the template                       *
*------------------------------------------------------------------------
*/

(function () {
    ("use strict");

    document.addEventListener("alpine:init", () => {
        Alpine.data("collapse", () => ({
            collapse: false,

            collapseSidebar() {
                this.collapse = !this.collapse;
            },
        }));
        Alpine.data("dropdown", (initialOpenState = false) => ({
            open: initialOpenState,

            toggle() {
                this.open = !this.open;
            },
        }));
        Alpine.data("modals", (initialOpenState = false) => ({
            open: initialOpenState,
            toggle() {
                this.open = !this.open;
            },
        }));

        // main - custom functions
        Alpine.data("main", (value) => { });

        Alpine.store("app", {
            // sidebar
            sidebar: false,
            toggleSidebar() {
                this.sidebar = !this.sidebar;
            },
            // Light and dark Mode
            mode: Alpine.$persist('light'),
            sidebarMode: Alpine.$persist('light'),
            layout: Alpine.$persist('vertical'),
            direction: Alpine.$persist('ltr'),
            showSettings: false,
            toggleMode(val) {
                if (!val) {
                    val = this.mode || "light"; // light And Dark

                }
                this.mode = val;

            },

            toggleFullScreen() {
                if (document.fullscreenElement) {
                    document.exitFullscreen();
                } else {
                    document.documentElement.requestFullscreen();
                }
            },

            setLayout() {
                // Set the layout based on current settings
                this.layout = this.layout || 'vertical';
                this.mode = this.mode || 'light';
                this.sidebarMode = this.sidebarMode || 'light';
                this.direction = this.direction || 'ltr';
                this.open = false;
            },

            resetLayout() {
                // Reset to default layout settings
                this.layout = 'vertical';
                this.mode = 'light';
                this.sidebarMode = 'light';
                this.direction = 'ltr';
                this.open = false;
            }
        });

        // sidebar menu activation
        const activeMenuFromStorage = localStorage.getItem('activeMenu');
        const activeMenu = activeMenuFromStorage ? activeMenuFromStorage : '';

        Alpine.store('sidebar', {
            activeMenu: activeMenu,
            toggleMenu(menu) {
                this.activeMenu = this.activeMenu === menu ? null : menu;
                if (menu === 'single') {
                    localStorage.removeItem('activeMenu');
                    return;
                }
                localStorage.setItem('activeMenu', this.activeMenu);
            },
        });

        function setActiveClass() {
            var currentPath = window.location.pathname;
            // Extract the last part of the path (to handle directories)
            var activeItem = document.querySelector('.sidebar ul li a[href="' + currentPath + '"]');
            if (activeItem) {
                activeItem.classList.add('active');
            } else {
                currentPath = currentPath.substring(currentPath.lastIndexOf('/') + 1);
                var activeItem = document.querySelector('.sidebar ul li a[href="' + currentPath + '"]');
                activeItem?.classList.add('active');
            }
        }

        Alpine.data('sidebarMenu', () => ({
            init() {
                setTimeout(() => {
                    setActiveClass();
                }, 0);
                setActiveClass();
            },
            isActive(menu) {
                return this.$store.sidebar.activeMenu === menu;
            },
            toggle(menu) {
                this.$store.sidebar.toggleMenu(menu);
            }
        }));
    });

    window.Alpine.start();

})();
