const loginUrl = '@Url.Action("Login", "Auth")';

document.addEventListener('DOMContentLoaded', function () {
    const token = localStorage.getItem('token');
    if (!token) {
        window.location.href = loginUrl;
        return;
    }

    // Get references to all interactive elements
    const statusFilter = document.getElementById('statusFilter');
    const sortBy = document.getElementById('sortBy');
    const logoutButton = document.getElementById('logoutButton');

    // Attach all event listeners
    statusFilter.addEventListener('change', loadOrders);
    sortBy.addEventListener('change', loadOrders);
    document.getElementById("logoutButton").onclick = function () {
        localStorage.removeItem("token");
        localStorage.removeItem("role");
        localStorage.removeItem("userId");
        window.location.href = "/Auth/login";
    }

    // Perform the initial data load
    loadOrders();
});

const getAuthHeaders = () => ({
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${localStorage.getItem('token') || ''}`
});

async function loadOrders() {
    const status = document.getElementById('statusFilter').value;
    const sortBy = document.getElementById('sortBy').value;
    const orderList = document.getElementById('orderList');

    orderList.innerHTML = '<div class="p-3 text-muted">Loading orders...</div>';

    try {
        const response = await fetch(`/api/orders?status=${status}&sortBy=${sortBy}`, { headers: getAuthHeaders() });

        if (!response.ok) {
            if (response.status === 401 || response.status === 403) {
                window.location.href = loginUrl;
                return;
            }
            throw new Error(`Failed to fetch orders. Status: ${response.status}`);
        }

        const orders = await response.json();
        orderList.innerHTML = '';

        if (!Array.isArray(orders) || orders.length === 0) {
            orderList.innerHTML = '<div class="p-3 text-muted">No orders found.</div>';
            document.getElementById('orderDetails').innerHTML = '<div class="card-body"><p class="text-muted">Select an order to see the details.</p></div>';
            return;
        }

        orders.forEach(order => {
            const orderItem = document.createElement('a');
            orderItem.href = '#';
            orderItem.className = 'list-group-item list-group-item-action order-item';
            orderItem.dataset.orderId = order.id;
            orderItem.innerHTML = `
                        <div class="d-flex w-100 justify-content-between">
                            <h5 class="mb-1">Order #${order.id}</h5>
                            <small>${new Date(order.createdAt).toLocaleString()}</small>
                        </div>
                        <p class="mb-1"><strong>Customer:</strong> ${order.customerName}</p>
                        <small><strong>Status:</strong> <span class="status-badge status-${order.status}">${order.status}</span></small>
                    `;

            orderItem.addEventListener('click', (e) => {
                e.preventDefault();
                document.querySelectorAll('.order-item').forEach(item => item.classList.remove('selected'));
                orderItem.classList.add('selected');
                loadOrderDetails(order.id);
            });
            orderList.appendChild(orderItem);
        });

    } catch (error) {
        orderList.innerHTML = '<div class="p-3 text-danger">Error loading orders.</div>';
        console.error("Error in loadOrders:", error);
    }
}

async function loadOrderDetails(orderId) {
    const orderDetails = document.getElementById('orderDetails');
    orderDetails.innerHTML = '<div class="card-body"><p class="text-muted">Loading details...</p></div>';

    try {
        const response = await fetch(`/api/orders/${orderId}`, { headers: getAuthHeaders() });
        if (!response.ok) throw new Error(`Failed to fetch details. Status: ${response.status}`);

        const order = await response.json();

        let totalPrice = (order.items || []).reduce((sum, item) => sum + (item.quantity * item.menuItemPrice), 0);

        const itemsHtml = (order.items || []).map(item => {
            const itemTotal = item.quantity * item.menuItemPrice;
            return `
                    <tr>
                        <td>${item.menuItemName || 'Archived Item'}</td>
                        <td>${item.quantity}</td>
                        <td>$${item.menuItemPrice.toFixed(2)}</td>
                        <td>$${itemTotal.toFixed(2)}</td>
                    </tr>`;
        }).join('');

        let actionButtonsHtml = '';
        switch (order.status) {
            case 'Pending':
                actionButtonsHtml = `
                            <button class="btn btn-info btn-sm" onclick="updateStatus(${order.id}, 'Submitted')">Submit Order</button>
                            <button class="btn btn-danger btn-sm" onclick="updateStatus(${order.id}, 'Cancelled')">Cancel Order</button>`;
                break;
            case 'Submitted':
                actionButtonsHtml = `
                            <button class="btn btn-primary btn-sm" onclick="updateStatus(${order.id}, 'Processing')">Mark as Processing</button>
                            <button class="btn btn-danger btn-sm" onclick="updateStatus(${order.id}, 'Cancelled')">Cancel Order</button>`;
                break;
            case 'Processing':
                actionButtonsHtml = `
                            <button class="btn btn-warning btn-sm" onclick="updateStatus(${order.id}, 'OutForDelivery')">Mark as Out for Delivery</button>
                            <button class="btn btn-danger btn-sm" onclick="updateStatus(${order.id}, 'Cancelled')">Cancel Order</button>`;
                break;
            case 'OutForDelivery':
                actionButtonsHtml = `<button class="btn btn-success btn-sm" onclick="updateStatus(${order.id}, 'Delivered')">Mark as Delivered</button>`;
                break;
        }

        orderDetails.innerHTML = `
                    <div class="card-header d-flex justify-content-between align-items-center">
                        <h5>Order #${order.id} Details</h5>
                        <span class="status-badge status-${order.status}">${order.status}</span>
                    </div>
                    <div class="card-body">
                        <p><strong>Customer:</strong> ${order.customerName}</p>
                        <p><strong>Phone:</strong> ${order.phoneNumber || 'Not Provided'}</p>
                        <p><strong>Address:</strong> ${order.deliveryAddress || 'Not Provided'}</p>
                        <p><strong>Notes:</strong> ${order.notes || 'N/A'}</p>
                        <hr/><h6>Items</h6>
                        <table class="table table-sm table-striped">
                            <thead><tr><th>Item</th><th>Qty</th><th>Price</th><th>Total</th></tr></thead>
                            <tbody>${itemsHtml}</tbody>
                        </table>
                        <h5 class="text-end mt-3">Total: $${totalPrice.toFixed(2)}</h5>
                    </div>
                    <div class="card-footer text-end">
                        ${actionButtonsHtml}
                    </div>
                `;
    } catch (error) {
        orderDetails.innerHTML = '<div class="card-body"><p class="text-danger">Error loading details.</p></div>';
        console.error("Error in loadOrderDetails:", error);
    }
}

async function updateStatus(orderId, newStatus) {
    try {
        const response = await fetch(`/api/orders/${orderId}/status`, {
            method: 'PUT',
            headers: getAuthHeaders(),
            body: JSON.stringify({ newStatus: newStatus })
        });
        if (!response.ok) throw new Error(`Failed to update status. Status: ${response.status}`);

        await loadOrders();

        const currentItem = document.querySelector(`.order-item[data-order-id='${orderId}']`);
        if (currentItem) {
            currentItem.classList.add('selected');
        }

        await loadOrderDetails(orderId);

    } catch (error) {
        alert('Error updating status. Please check the console for details.');
        console.error("Error in updateStatus:", error);
    }
}