/*
Template Name: Sliced - Admin & Dashboard Template
Author: SRBThemes
Version: 3.0.0
File: contact init Js File
*/




// Initialize Alpine.js

 function contactsTable() {
    return {
        contacts: [],
        sortBy: '',
        sortDirection: 'asc',
        sortClasses: {
            'asc': '↑',
            'desc': '↓'
        },
        selectAll: false,
        selectedItems: [],
        currentPage: 1,
        itemsPerPage: 12,
        toggleAll() {
            this.selectedItems = this.selectAll ? [...this.displayedContacts] : [];
        },
        get totalPages() {
            return Math.ceil(this.contacts.length / this.itemsPerPage);
        },
        get displayedContacts() {
            const start = (this.currentPage - 1) * this.itemsPerPage;
            const end = start + this.itemsPerPage;
            return this.contacts.slice(start, end);
        },
        get showingStart() {
            return Math.min((this.currentPage - 1) * this.itemsPerPage + 1, this.contacts.length);
        },
        get showingEnd() {
            return Math.min(this.currentPage * this.itemsPerPage, this.contacts.length);
        },
        init() {
            this.fetchData('assets/js/pages/json/contact.json');
        },
        fetchData(jsonPath) {
            fetch(jsonPath)
                .then(response => response.json())
                .then(data => {
                    let startID = 22697; // Starting ID
                    data.forEach((contact, index) => {
                        // Generate categoryID starting from "PEC-24151"
                        contact.contactID = "SRBC-" + (startID + index).toString();
                    });
                    this.contacts = data;
                });
        },
        sort(column) {
            if (column === this.sortBy) {
                this.sortDirection = this.sortDirection === 'asc' ? 'desc' : 'asc';
            } else {
                this.sortDirection = 'asc';
                this.sortBy = column;
            }

            this.contacts.sort((a, b) => {
                const valueA = a[column];
                const valueB = b[column];
                let comparison = 0;
                if (valueA > valueB) {
                    comparison = 1;
                } else if (valueA < valueB) {
                    comparison = -1;
                }
                return this.sortDirection === 'desc' ? comparison * -1 : comparison;
            });
        },
        prevPage() {
            if (this.currentPage > 1) {
                this.currentPage--;
            }
        },
        nextPage() {
            if (this.currentPage < this.totalPages) {
                this.currentPage++;
            }
        },
        gotoPage(page) {
            this.currentPage = page;
        }
    };
  }


document.addEventListener('alpine:init', () => {
    window.Alpine.data('contactsTable', contactsTable);
})
// window.Alpine.data('contactsTable', contactsTable);
