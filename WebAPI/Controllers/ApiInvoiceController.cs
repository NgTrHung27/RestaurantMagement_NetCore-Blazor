﻿using CodeFirst.Data;
using CodeFirst.Models;
using CodeFirst.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Mail;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using static MudBlazor.CategoryTypes;

namespace WebAPI.Controllers
{
    [Route("api/invoice")]
    [ApiController]
    public class ApiInvoiceController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public ApiInvoiceController(ApplicationDbContext context)
        {
            _context = context;
        }
        [HttpGet("/api/time/get-hall-id")]
        public async Task<ActionResult<List<TimeOfDay>>> GetTimeOfDayByHallId(int hallId)
        {
            var timeOfDayList = await _context.TimeOfDay
                .Where(t => t.HallId == hallId)
                .ToListAsync();

            if (timeOfDayList == null || !timeOfDayList.Any())
            {
                return NotFound();
            }

            return Ok(timeOfDayList);
        }

        [HttpPost("/api/invoice/repayment/{id}")]
        public async Task<IActionResult> Repayment(int id)
        {
            var invoice =  _context.Invoice.FirstOrDefault(x => x.InvoiceID == id);
            _currentInvoiceId = invoice.InvoiceID;

            return Ok(new { message = "Đã hủy đơn hàng" });
        }

        [HttpPost("/api/invoice/cancel/{id}")]
        public async Task<IActionResult> CancelOrder(int id)
        {
            var invoice = _context.Invoice.FirstOrDefault(x => x.InvoiceID == id);
            invoice.OrderStatus = "Đã hủy đơn hàng";

            _context.Update(invoice);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Đã hủy đơn hàng" });
        }

        [HttpPost("checked")]
        public IActionResult CheckDuplicateInvoice([FromBody] CheckDuplicateInvoice request)
        {
            var existingInvoice = _context.Invoice.FirstOrDefault(i =>
                i.AttendanceDate.Value.Date == request.AttendanceDate.Date &&
                i.BranchId == request.BranchId &&
                i.HallId == request.HallId);

            if (existingInvoice != null)
            {
                return BadRequest(new { message = "Chi nhánh và sảnh ngày hôm đó đã có người đặt" });
            }

            return Ok(new { message = "Không có hóa đơn trùng." });
        }
        static int? _currentInvoiceId;
        [HttpPost]
        public async Task<IActionResult> CreateInvoiceAndOrderMenus([FromBody] InvoiceAndOrderMenusRequest request)
        {

            // Kiểm tra xem đã có hóa đơn nào cùng ngày, cùng sảnh và cùng chi nhánh chưa
            var existingInvoice = _context.Invoice
                .FirstOrDefault(i => i.AttendanceDate.HasValue &&
                    i.AttendanceDate.Value.Date == request.AttendanceDate.Value.Date &&
                    i.BranchId == request.BranchId &&
                    i.HallId == request.HallId);
            if (existingInvoice != null)
            {
                return BadRequest(new { message = "Đã có hóa đơn được tạo trong cùng một ngày, cùng chi nhánh và cùng sảnh." });
            }
            // Tạo một đối tượng Invoice từ dữ liệu gửi từ React
            var invoice = new Invoice
            {
                UserId = request.UserId,
                BranchId = (int)request.BranchId,
                HallId = (int)request.HallId,
                InvoiceDate = DateTime.Now,
                Total = request.Total,
                TotalBeforeDiscount = request.TotalBeforeDiscount,
                FullName = request.FullName,
                PhoneNumber = request.PhoneNumber,
                Note = request.Note,
                PaymentStatus = false,
                TimeHall = request.TimeHall
            };

          


            // Kiểm tra ngày AttendanceDate
            if (request.AttendanceDate.HasValue)
            {
                // Kiểm tra nếu AttendanceDate cách ngày hiện tại ít nhất 20 ngày
                TimeSpan difference = request.AttendanceDate.Value - DateTime.Now;
                if (difference.Days < 20)
                {
                    return BadRequest(new { message = "Ngày đến tham dự phải cách ngày hiện tại ít nhất 20 ngày." });
                }
                invoice.AttendanceDate = request.AttendanceDate;
            }

            // Thêm đối tượng Invoice vào DbContext và lưu vào cơ sở dữ liệu
            _context.Invoice.Add(invoice);
            await _context.SaveChangesAsync();


            var newProjectByInvoice = new Project
            {
                Name = invoice.InvoiceID.ToString(),
            };
            _context.Projects.Add(newProjectByInvoice);
            await _context.SaveChangesAsync();

            // Tạo danh sách các món đã đặt từ dữ liệu gửi từ React
            var orderMenus = request.OrderMenus.Select(orderMenu => new OrderMenu
            {
                InvoiceID = invoice.InvoiceID, // Liên kết với hóa đơn mới tạo
                MenuId = orderMenu.MenuID
            }).ToList();


            // Thêm danh sách các món đã đặt vào DbContext và lưu vào cơ sở dữ liệu
            _context.OrderMenu.AddRange(orderMenus);
            await _context.SaveChangesAsync();

            var orderServices = request.OrderServices.Select(orderService => new OrderService
            {
                InvoiceID = invoice.InvoiceID, // Liên kết với hóa đơn mới tạo
                ServiceId = orderService.ServiceId
            }).ToList();

            // Thêm danh sách các món đã đặt vào DbContext và lưu vào cơ sở dữ liệu
            _context.OrderService.AddRange(orderServices);
            await _context.SaveChangesAsync();
            SendMail(request, invoice, orderMenus, orderServices);


            //if (request.InvoiceCode != null && request.InvoiceCode.Any())
            //{
            // Tạo danh sách các đối tượng InvoiceCode và thêm thông tin mã giảm giá
            var invoiceCodes = request.InvoiceCodeRequest.Select(codeId => new InvoiceCode
            {
                InvoiceId = invoice.InvoiceID,
                CodeId = codeId.CodeId
            }).ToList();

            // Thêm danh sách các InvoiceCode vào DbContext và lưu vào cơ sở dữ liệu
            _context.InvoiceCode.AddRange(invoiceCodes);
            await _context.SaveChangesAsync();
            //}
            _currentInvoiceId = invoice.InvoiceID;
            return Ok(new { message = "Hóa đơn và món đã đặt đã được tạo thành công!" });
        }

        [HttpGet("payment-return-url")]
        public IActionResult ReturnPaymentUrl()
        {
            try
            {
                var paymentUrl = "http://localhost:3000/payment-success";
                var getInvoice = _context.Invoice.FirstOrDefault(x => x.InvoiceID == _currentInvoiceId);
                getInvoice.PaymentStatus = true;
                _context.Update(getInvoice);
                _context.SaveChanges();
                return RedirectPermanent(paymentUrl);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpGet("booked-hall")]
        public IActionResult GetBookedHalls()
        {
            // Lấy ngày hiện tại
            var currentDate = DateTime.Now.Date;

            // Truy vấn cơ sở dữ liệu để lấy danh sách các sảnh đã có người đặt từ ngày hiện tại trở về sau và sắp xếp theo AttendanceDate tăng dần
            var bookedHalls = _context.Invoice
                .Where(i => i.HallId != null && i.AttendanceDate >= currentDate)
                .Select(i => new
                {
                    HallId = i.HallId,
                    HallName = i.Hall.Name,
                    BranchName = i.Branch.Name,
                    BookingDate = i.AttendanceDate
                })
                .OrderBy(i => i.BookingDate)
                .ToList();

            return Ok(bookedHalls);
        }
     
        void SendMail(InvoiceAndOrderMenusRequest request, Invoice invoiceRecv, List<OrderMenu> orderMenus, List<OrderService> orderServices)
        {
            var invoice = _context.Invoice.FirstOrDefault(x => x.InvoiceID == invoiceRecv.InvoiceID);
            var invoiceWithBranch = _context.Branch.FirstOrDefault(x => x.BranchId == request.BranchId);
            var invoiceWithHall = _context.Hall.FirstOrDefault(x => x.HallId == request.HallId);
                           
            StringBuilder body = new StringBuilder();
            body.AppendLine("<html><head>");
            body.AppendLine("<style>");
            body.AppendLine("body { font-family: Arial, sans-serif; margin: 0; padding: 0; }");
            body.AppendLine(".container { width: 80%; margin: auto; overflow: hidden; }");
            body.AppendLine("header { background: white; color: white; padding-top: 30px; min-height: 70px; border-bottom: #D4BB72 4px solid; }");
            body.AppendLine("header a { color: white; text-decoration: none; text-transform: uppercase; font-size: 16px; }");
            body.AppendLine("header ul { padding: 0; margin: 0; float: right; margin-top: 20px; list-style: none; }");
            body.AppendLine("header #branding { float: left; margin: 0; padding: 0; }");
            body.AppendLine("header #branding img { height: 80px; }");
            body.AppendLine("header .highlight, header .current a { color: #E8BD72; font-weight: bold; }");
            body.AppendLine("header a:hover { color: #ffffff; font-weight: bold; }");
            body.AppendLine(".main { padding: 0; }");
            body.AppendLine(".main h2 { color: #333; }");
            body.AppendLine(".main p { font-size: 18px; color: #666; }");
            body.AppendLine("table { border-collapse: collapse; width: 100%; }");
            body.AppendLine("th, td { border: 1px solid #dddddd; text-align: left; padding: 12px; font-size: 16px; }");
            body.AppendLine("th { background-color: #f2f2f2; }");
            body.AppendLine("</style>");
            body.AppendLine("</head><body>");
            body.AppendLine("<header>");
            body.AppendLine("<div class=\"container\">");
            body.AppendLine("<div id=\"branding\">");
            // Thêm ảnh logo vào giữa header
            body.AppendLine("<h1><span class=\"highlight\">Nhà hàng tiệc cưới</span></h1>");
            body.AppendLine("</div>");
            body.AppendLine("</div>");
            body.AppendLine("</header>");
            body.AppendLine("<div class=\"container main\">");
            body.AppendLine("<h2>Chi tiết đơn hàng:</h2>");

            body.AppendLine($"<p><strong>Mã đơn hàng:</strong> {invoice.InvoiceID.ToString()}</p>");
            var customer = _context.ApplicationUser.FirstOrDefault(x => x.Id == request.UserId);

            body.AppendLine($"<p><strong>Email đặt hàng:</strong> {customer.Email}</p>");
            body.AppendLine($"<p><strong>Số điện thoại:</strong> {customer.PhoneNumber}</p>");

            body.AppendLine($"<p><strong>Ngày đặt hàng:</strong> {invoice.InvoiceDate}</p>");
            body.AppendLine($"<p><strong>Chi nhánh đã đặt:</strong> {invoiceWithBranch.Name}</p>");
            body.AppendLine($"<p><strong>Sản phẩm:</strong></p>");


            body.AppendLine("<table>");
            body.AppendLine("<tr><th>Hình ảnh</th><th>Tên sản phẩm</th><th>Giá bán</th></tr>");

            body.AppendLine($"<tr>");
            body.AppendLine($"<td style='width: 150px'><img src='{invoiceWithHall.Image}' alt='Logo' style='width: 100%; height: 120px;' /></td>");
            body.AppendLine($"<td>{invoiceWithHall.Name}</td>");
            body.AppendLine($"<td>{invoiceWithHall.Price.Value.ToString("#,##0")} VND</td>");
            body.AppendLine($"</tr>");


            // danh sách món ăn đặt nhà hàng
            foreach (var orderMenu in orderMenus)
            {
                var item = _context.MenuEntity.Find(orderMenu.MenuId);
                body.AppendLine($"<tr>");
                body.AppendLine($"<td style='width: 150px'><img src='{item.Image}' alt='Logo' style='width: 100%; height: 120px;' /></td>");
                body.AppendLine($"<td>{item.Name}</td>");
                body.AppendLine($"<td>{item.Price.Value.ToString("#,##0")} VND</td>");
                body.AppendLine($"</tr>");
            }
            // danh sách dịch vụ đặt nhà hàng
            foreach (var orderService in orderServices)
            {
                var item = _context.ServiceEntity.Find(orderService.ServiceId);
                body.AppendLine($"<tr>");
                body.AppendLine($"<td style='width: 150px'><img src='{item.Image}' alt='Logo' style='width: 100%; height: 120px;' /></td>");
                body.AppendLine($"<td>{item.Name}</td>");
                body.AppendLine($"<td>{item.Price.Value.ToString("#,##0")} VND</td>");
                body.AppendLine($"</tr>");
            }
            body.AppendLine($"<tr><td colspan='2'><strong>Tổng tiền sau khi áp dụng mã giảm:</strong></td><td>{invoice.Total.Value.ToString("#,##0")} VND</td></tr>");


            body.AppendLine("</table>");
            body.AppendLine("</div>");

            body.AppendLine("<footer style=\"margin-top:20px;background: #262626; color: white; padding: 20px 0;\">");
            body.AppendLine("<div class=\"container\">");
            body.AppendLine("<p>&copy; 2023 Khóa luận Khoa Học Máy Tính. </p>");
            body.AppendLine("</div>");
            body.AppendLine("</footer>");

            body.AppendLine("</body></html>");

            // Gửi email
            MailMessage mail = new MailMessage();
            //mail.To.Add(customer.Email.ToString());
            mail.To.Add("minhnguyen20020524@gmail.com"); // email người nhận 

            mail.From = new MailAddress("duatreodaiduongden@gmail.com");// email người gửi 
            mail.Subject = "Nhà hàng tiệc cưới - CHI TIẾT ĐƠN HÀNG";
            mail.Body = body.ToString();
            mail.IsBodyHtml = true; // Bật chế độ HTML

            SmtpClient smtp = new SmtpClient("sandbox.smtp.mailtrap.io");
            smtp.EnableSsl = true;
            smtp.Port = 2525;
            smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
            smtp.Credentials = new NetworkCredential("f53ec0c5d129dd", "647d8437d3d40c");

            // SmtpClient smtp = new SmtpClient("smtp.gmail.com"); // máy chủ smtp của google
            // smtp.EnableSsl = true;
            // smtp.Port = 587; // port client mặc định hầu như máy nào cũng vậy
            // smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
            // smtp.Credentials = new NetworkCredential("duatreodaiduongden@gmail.com", "aiyt kzuj xpbq ygda"); // từ năm 2022 trở đi dùng mật khẩu do gmail cấp 

            try
            {
                smtp.Send(mail);
            }
            catch (Exception ex)
            {
                // Xử lý nếu có lỗi khi gửi mail
            }
        }

        [HttpGet("get-invoice/{userId}")]
        public IActionResult GetInvoicesByUser(string userId)
        {
            try
            {
                // Truy vấn danh sách hóa đơn dựa trên UserId và kèm theo thông tin OrderMenu và OrderService
                var invoices = _context.Invoice
                    .Where(i => i.UserId == userId)
                    .Include(i => i.Branch)
                    .Include(i => i.Hall)
                    .Include(i => i.OrderMenus) // Kèm thông tin OrderMenu
                        .ThenInclude(om => om.MenuEntity) // Kèm thông tin MenuEntity
                    .Include(i => i.OrderServices) // Kèm thông tin OrderService
                        .ThenInclude(os => os.ServiceEntity) // Kèm thông tin ServiceEntity
                    .ToList();

                return Ok(invoices);
            }
            catch (Exception ex)
            {
                // Xử lý lỗi nếu cần
                return StatusCode(500, new { message = "Lỗi server: " + ex.Message });
            }
        }
        [HttpGet("promo-code")]
        public async Task<ActionResult<IEnumerable<Code>>> GetPromoCodes()
        {
            var promoCodes = await _context.Code.ToListAsync();
            return Ok(promoCodes);
        }
        [HttpPost("use-promo-code")]
        public async Task<ActionResult> UsePromoCode(int codeId)
        {
            var code = await _context.Code.FindAsync(codeId);

            if (code != null && code.Quantity > 0)
            {
                code.Quantity--; // Giảm số lượng mã giảm giá
                if (code.Quantity <= 0)
                {
                    _context.Code.Remove(code); // Xóa mã nếu hết lượt sử dụng
                }
                await _context.SaveChangesAsync();
                return Ok("Mã giảm giá đã được sử dụng.");
            }

            return BadRequest("Mã giảm giá không hợp lệ hoặc đã hết lượt sử dụng.");
        }

        
    }
}
