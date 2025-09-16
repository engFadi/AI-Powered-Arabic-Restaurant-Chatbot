using ProjectE.Models.Entities;

public interface ITokenService
{
    string GenerateToken(UserEntity user);
}
