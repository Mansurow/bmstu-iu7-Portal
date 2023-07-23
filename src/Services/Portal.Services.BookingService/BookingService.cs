﻿using Portal.Common.Models;
using Portal.Common.Models.Enums;
using Portal.Database.Repositories.Interfaces;
using Portal.Services.BookingService.Exceptions;
using System.Globalization;
using Portal.Services.PackageService.Exceptions;
using Portal.Services.ZoneService.Exceptions;

namespace Portal.Services.BookingService
{
    /// <summary>
    ///  Сервис бронирования зон
    /// </summary>
    public class BookingService : IBookingService
    {
        private readonly IBookingRepository _bookingRepository;
        private readonly IPackageRepository _packageRepository;
        private readonly IZoneRepository _zoneRepository;

        public BookingService(IBookingRepository bookingRepository, 
            IPackageRepository packageRepository,
            IZoneRepository zoneRepository)
        {
            _bookingRepository = bookingRepository ?? throw new ArgumentNullException(nameof(bookingRepository));
            _packageRepository = packageRepository ?? throw new ArgumentNullException(nameof(packageRepository));
            _zoneRepository = zoneRepository ?? throw new ArgumentNullException(nameof(zoneRepository));
        }

        public async Task<List<Booking>> GetAllBookingAsync()
        {
            var bookings = await _bookingRepository.GetAllBookingAsync();

            await UpdateNoActualBookingsAsync(bookings);

            return bookings;
        }

        public async Task<List<Booking>> GetBookingByUserAsync(Guid userId)
        {
            var bookings = await _bookingRepository.GetBookingByUserAsync(userId);

            await UpdateNoActualBookingsAsync(bookings);

            return bookings;
        }
        
        private async Task UpdateNoActualBookingsAsync(IEnumerable<Booking> bookings)
        {
            foreach (var b in bookings.Where(b => b.IsBookingExpired() 
                                                  && b.Status is not BookingStatus.NoActual))
            {
                b.ChangeStatus(BookingStatus.NoActual);
                await _bookingRepository.UpdateNoActualBookingAsync(b.Id);
            }
        }
        
        public async Task<List<Booking>> GetBookingByZoneAsync(Guid zoneId)
        {
            var bookings = await _bookingRepository.GetBookingByZoneAsync(zoneId);

            await UpdateNoActualBookingsAsync(bookings);

            return bookings;
        }
        
        public async Task<Booking> GetBookingByIdAsync(Guid bookingId)
        {
            var booking = await _bookingRepository.GetBookingByIdAsync(bookingId);
            if (booking is null)
            {
                throw new BookingNotFoundException($"Bookings with id: {bookingId} not found");
            }

            if (booking.IsBookingExpired())
            {
                booking.ChangeStatus(BookingStatus.NoActual);
                await _bookingRepository.UpdateNoActualBookingAsync(booking.Id);
            }

            return booking;
        }

        public async Task<List<FreeTime>> GetFreeTimeAsync(Guid zoneId, DateOnly date)
        {
            var bookings = (await GetBookingByZoneAsync(zoneId))
                .FindAll(e => e.Date == date && e.Status != BookingStatus.NoActual)
                .OrderBy(e => e.StartTime).ToList();
            
            var freeTimes = new List<FreeTime>();
            var startTimeWork = new TimeOnly(8, 0);
            var endTimeWork = new TimeOnly(23, 0);
            for (var i = 0; i < bookings.Count; i++)
            {
                FreeTime addFreeTime;
                if (i == 0)
                {
                    addFreeTime = new FreeTime(startTimeWork, bookings[i].StartTime);
                }
                else if (i == bookings.Count - 1)
                {
                    addFreeTime = new FreeTime(bookings[i].EndTime, endTimeWork);
                }
                else
                {
                    addFreeTime = new FreeTime(bookings[i - 1].EndTime, bookings[i].StartTime);
                }
                
                if (addFreeTime.EndTime - addFreeTime.StartTime >= new TimeSpan(1, 0, 0))
                    freeTimes.Add(addFreeTime);
            }

            return freeTimes.OrderBy(f => f.StartTime).ToList();
        }

        public async Task<Guid> AddBookingAsync(Guid userId, Guid zoneId, Guid packageId, string date, string startTime, string endTime)
        {
            var culture = CultureInfo.CreateSpecificCulture("ru-RU");
            var dateRu = DateOnly.Parse(date, culture);
            var startTimeRu = TimeOnly.Parse(startTime, culture);
            var endTimeRu = TimeOnly.Parse(endTime, culture);
            
            var zone = await _zoneRepository.GetZoneByIdAsync(zoneId);
            if (zone is null)
            {
                throw new ZoneNotFoundException($"Zone with id: {zoneId} not found");
            }
            
            var package = await  _packageRepository.GetPackageByIdAsync(packageId);
            if (package is null)
            {
                throw new PackageNotFoundException($"Package with id: {zoneId} not found");
            }
            
            var booking = (await _bookingRepository.GetBookingByUserAndZoneAsync(userId, zoneId))
                .FirstOrDefault(b => b.Date == dateRu);
            if (booking is not null)
            {
                throw new BookingExistsException($"User with id: {userId} reversed for zone with id: {zoneId}");
            }
                
            if (!await IsFreeTimeAsync(dateRu, startTimeRu, endTimeRu))
            {
                throw new BookingReversedException($"Zone full or partial reversed on {dateRu} from {startTime} to {endTime}");
            }
            
            var newBooking = new Booking(Guid.NewGuid(), zoneId, userId, packageId,
                zone.Limit, BookingStatus.TemporaryReserved,
                dateRu, startTimeRu, endTimeRu);
            await _bookingRepository.InsertBookingAsync(newBooking);

            return newBooking.Id;
        }
        
        public async Task<bool> IsFreeTimeAsync(DateOnly date, TimeOnly startTime, TimeOnly endTime)
        {
            var bookings =  (await _bookingRepository.GetAllBookingAsync())
                .FindAll(b => b.Date == date);
            
            return bookings.Count != 0 
                   && bookings.All(b => (b.StartTime < startTime && b.EndTime <= startTime) 
                                        || (b.StartTime >= endTime && b.EndTime > startTime));
        }
        
        public async Task ChangeBookingStatusAsync(Guid bookingId, BookingStatus status)
        {
            var booking = await GetBookingByIdAsync(bookingId);

            if (!booking.IsSuitableStatus(status))
            {
                throw new BookingNotSuitableStatusException($"Changing for booking with id: {bookingId} for user: {booking.UserId} isn't suitable for next step");
            }
            
            booking.ChangeStatus(status);
            await UpdateBookingAsync(booking);
        }

        public async Task UpdateBookingAsync(Booking updateBooking)
        {
            var booking = await _bookingRepository.GetBookingByIdAsync(updateBooking.Id);
            if (booking is null)
            {
                throw new BookingNotFoundException($"Booking with id: {updateBooking.Id} not found");
            }

            var zone = await _zoneRepository.GetZoneByIdAsync(updateBooking.ZoneId);
            if (zone is not null && zone.Limit < updateBooking.AmountPeople)
            {
                throw new BookingExceedsLimitException($"Exceed limit amount of people for booking with id: {updateBooking.Id}");
            }
            
            if (booking.IsChangeDateTime(updateBooking))
            {
                throw new BookingChangeDateTimeException($"Changing date or time for booking with id: {booking.Id}");
            }   
            
            // if (booking.Status != BookingStatus.NoActual && booking.IsBookingExpired())
            //     booking.ChangeStatus(BookingStatus.NoActual);

            await _bookingRepository.UpdateBookingAsync(updateBooking);
        }

        public async Task RemoveBookingAsync(Guid bookingId)
        {
            var booking = await _bookingRepository.GetBookingByIdAsync(bookingId);
            if (booking is null)
            {
                throw new BookingNotFoundException($"Booking with id: {bookingId} not found");
            }

            await _bookingRepository.DeleteBookingAsync(bookingId);
        }
    }
}
